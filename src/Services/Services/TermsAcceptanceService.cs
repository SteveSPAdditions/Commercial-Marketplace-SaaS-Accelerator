// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Net;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Terms-acceptance gate. See <see cref="ITermsAcceptanceService"/>.
///
/// Semantics (agreed 2026-09-30):
///   - One acceptance per subscription, ever. A later change to the configured document versions
///     does NOT re-prompt: the customer agreed to the offer's terms at purchase and this portal
///     checkbox is a confirmed acknowledgement, not a fresh contract. Versions are recorded on the
///     row purely so the record shows what was presented.
///   - Not carried over on resubscribe (a resubscribe is a new purchase; one click to re-confirm).
///   - Fail closed: with the gate on and either document URL blank, the step is unactionable and
///     Setup stays locked. The TermsAcceptanceRequired switch is the emergency override.
/// </summary>
public class TermsAcceptanceService : ITermsAcceptanceService
{
    public const string RequiredKey = "TermsAcceptanceRequired";
    public const string MicrosoftContractTitleKey = "TermsMicrosoftContractTitle";
    public const string MicrosoftContractUrlKey = "TermsMicrosoftContractUrl";
    public const string MicrosoftContractVersionKey = "TermsMicrosoftContractVersion";
    public const string PublisherAmendmentTitleKey = "TermsPublisherAmendmentTitle";
    public const string PublisherAmendmentUrlKey = "TermsPublisherAmendmentUrl";
    public const string PublisherAmendmentVersionKey = "TermsPublisherAmendmentVersion";
    public const string EmailEnabledKey = "IsEmailEnabledForTermsAcceptance";

    /// <summary>EmailTemplate.Status of the confirmation email.</summary>
    public const string EmailTemplateStatus = "TermsAccepted";

    private const string DefaultMicrosoftContractTitle = "Microsoft Standard Contract for the Microsoft commercial marketplace";
    private const string DefaultPublisherAmendmentTitle = "Publisher amendment to the Standard Contract";

    private readonly ISubscriptionTermsAcceptanceRepository acceptanceRepo;
    private readonly ISubscriptionsRepository subscriptionsRepo;
    private readonly ISubscriptionLogRepository subscriptionLogRepo;
    private readonly IUsersRepository usersRepo;
    private readonly IApplicationConfigRepository configRepo;
    private readonly IEmailTemplateRepository emailTemplateRepo;
    private readonly IEmailService emailService;
    private readonly ApplicationLogService applicationLogService;
    private readonly SaaSApiClientConfiguration saasConfig;
    private readonly ILogger<TermsAcceptanceService> logger;

    public TermsAcceptanceService(
        ISubscriptionTermsAcceptanceRepository acceptanceRepo,
        ISubscriptionsRepository subscriptionsRepo,
        ISubscriptionLogRepository subscriptionLogRepo,
        IUsersRepository usersRepo,
        IApplicationConfigRepository configRepo,
        IEmailTemplateRepository emailTemplateRepo,
        IEmailService emailService,
        IApplicationLogRepository applicationLogRepo,
        SaaSApiClientConfiguration saasConfig,
        ILogger<TermsAcceptanceService> logger)
    {
        this.acceptanceRepo = acceptanceRepo;
        this.subscriptionsRepo = subscriptionsRepo;
        this.subscriptionLogRepo = subscriptionLogRepo;
        this.usersRepo = usersRepo;
        this.configRepo = configRepo;
        this.emailTemplateRepo = emailTemplateRepo;
        this.emailService = emailService;
        this.applicationLogService = new ApplicationLogService(applicationLogRepo);
        this.saasConfig = saasConfig;
        this.logger = logger;
    }

    public TermsAcceptanceStatus GetConfiguration()
    {
        // Missing row => gate ON. A deployment that has not run the seeding migration must not
        // silently skip the gate.
        var requiredRaw = this.configRepo.GetValueByName(RequiredKey);
        var required = !bool.TryParse(requiredRaw, out var r) || r;

        var microsoft = new TermsDocument
        {
            Title = FirstNonBlank(this.configRepo.GetValueByName(MicrosoftContractTitleKey), DefaultMicrosoftContractTitle),
            Url = (this.configRepo.GetValueByName(MicrosoftContractUrlKey) ?? string.Empty).Trim(),
            Version = (this.configRepo.GetValueByName(MicrosoftContractVersionKey) ?? string.Empty).Trim(),
        };
        var amendment = new TermsDocument
        {
            Title = FirstNonBlank(this.configRepo.GetValueByName(PublisherAmendmentTitleKey), DefaultPublisherAmendmentTitle),
            Url = (this.configRepo.GetValueByName(PublisherAmendmentUrlKey) ?? string.Empty).Trim(),
            Version = (this.configRepo.GetValueByName(PublisherAmendmentVersionKey) ?? string.Empty).Trim(),
        };

        return new TermsAcceptanceStatus
        {
            Required = required,
            IsConfigured = microsoft.IsConfigured && amendment.IsConfigured,
            MicrosoftContract = microsoft,
            PublisherAmendment = amendment,
        };
    }

    public TermsAcceptanceStatus GetStatus(Guid ampSubscriptionId)
    {
        var status = this.GetConfiguration();
        status.AmpSubscriptionId = ampSubscriptionId;
        status.Acceptance = this.acceptanceRepo.GetLatestByAmpSubscriptionId(ampSubscriptionId);
        status.Accepted = status.Acceptance != null;
        return status;
    }

    public TermsAcceptanceResult Record(Guid ampSubscriptionId, TermsAcceptanceRequest request)
    {
        if (request == null)
        {
            return TermsAcceptanceResult.Fail("Nothing to record.");
        }

        var status = this.GetStatus(ampSubscriptionId);
        if (status.Accepted)
        {
            // Idempotent: a double-submit (or a second admin) does not create a second legal record.
            return new TermsAcceptanceResult { Success = true, Acceptance = status.Acceptance };
        }

        if (!status.IsConfigured)
        {
            this.logger.LogError(
                "Terms acceptance attempted for {SubscriptionId} but the documents are not configured ({MsKey} / {AmendKey}).",
                ampSubscriptionId,
                MicrosoftContractUrlKey,
                PublisherAmendmentUrlKey);
            return TermsAcceptanceResult.Fail("The terms documents are not configured on this portal. Please contact support.");
        }

        if (!request.MicrosoftContractAccepted || !request.PublisherAmendmentAccepted)
        {
            return TermsAcceptanceResult.Fail("Please confirm you agree to both documents before continuing.");
        }

        var subscription = this.subscriptionsRepo.GetById(ampSubscriptionId, true);
        if (subscription == null)
        {
            return TermsAcceptanceResult.Fail("Subscription not found.");
        }

        var acceptance = new SubscriptionTermsAcceptance
        {
            AmpSubscriptionId = ampSubscriptionId,
            TenantId = subscription.PurchaserTenantId ?? Guid.Empty,
            AcceptedUtc = DateTime.UtcNow,
            AcceptedByUpn = Truncate(request.AcceptedByUpn, 255),
            AcceptedByObjectId = Truncate(request.AcceptedByObjectId, 64),
            AcceptedByDisplayName = Truncate(request.AcceptedByDisplayName, 255),
            MicrosoftContractAccepted = true,
            MicrosoftContractUrl = Truncate(status.MicrosoftContract.Url, 1000),
            MicrosoftContractVersion = Truncate(status.MicrosoftContract.Version, 64),
            PublisherAmendmentAccepted = true,
            PublisherAmendmentTitle = Truncate(status.PublisherAmendment.Title, 255),
            PublisherAmendmentUrl = Truncate(status.PublisherAmendment.Url, 1000),
            PublisherAmendmentVersion = Truncate(status.PublisherAmendment.Version, 64),
            IpAddress = Truncate(request.IpAddress, 64),
            UserAgent = Truncate(request.UserAgent, 512),
            Source = Truncate(string.IsNullOrWhiteSpace(request.Source) ? "Setup" : request.Source, 32),
        };

        this.acceptanceRepo.Add(acceptance);

        this.WriteAuditLog(subscription, acceptance);
        this.applicationLogService.AddApplicationLog(
            $"Terms accepted for subscription {ampSubscriptionId} by {acceptance.AcceptedByUpn} "
            + $"(Microsoft Standard Contract {DescribeVersion(acceptance.MicrosoftContractVersion)}; "
            + $"{acceptance.PublisherAmendmentTitle} {DescribeVersion(acceptance.PublisherAmendmentVersion)}).")
            .ConfigureAwait(false);

        this.SendConfirmationEmail(subscription, acceptance);

        return new TermsAcceptanceResult { Success = true, Acceptance = acceptance };
    }

    private void WriteAuditLog(Subscriptions subscription, SubscriptionTermsAcceptance acceptance)
    {
        try
        {
            int? userId = null;
            if (!string.IsNullOrEmpty(acceptance.AcceptedByUpn))
            {
                userId = this.usersRepo.GetPartnerDetailFromEmail(acceptance.AcceptedByUpn)?.UserId;
            }

            this.subscriptionLogRepo.Save(new SubscriptionAuditLogs
            {
                Attribute = SubscriptionLogAttributes.TermsAccepted.ToString(),
                SubscriptionId = subscription.Id,
                OldValue = "None",
                NewValue = $"Accepted by {acceptance.AcceptedByUpn} on {acceptance.AcceptedUtc:yyyy-MM-dd HH:mm} UTC "
                    + $"(Microsoft Standard Contract {DescribeVersion(acceptance.MicrosoftContractVersion)}; "
                    + $"amendment {DescribeVersion(acceptance.PublisherAmendmentVersion)})",
                CreateBy = userId,
                CreateDate = DateTime.Now,
            });
        }
        catch (Exception ex)
        {
            // The acceptance row is the legal record and is already committed; the audit-log row
            // is a convenience view of it. Never let it fail the acceptance.
            this.logger.LogError(ex, "Failed to write the TermsAccepted audit-log row for {SubscriptionId}.", subscription.AmpsubscriptionId);
        }
    }

    /// <summary>
    /// Confirmation to the person who accepted, with links to the exact documents they agreed to.
    /// Gated by IsEmailEnabledForTermsAcceptance (default true) and the presence of an active
    /// "TermsAccepted" template. The template's CC/BCC fields let the publisher keep a copy.
    /// </summary>
    private void SendConfirmationEmail(Subscriptions subscription, SubscriptionTermsAcceptance acceptance)
    {
        try
        {
            if (bool.TryParse(this.configRepo.GetValueByName(EmailEnabledKey), out var enabled) && !enabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(acceptance.AcceptedByUpn) || !acceptance.AcceptedByUpn.Contains('@'))
            {
                this.logger.LogWarning("Terms acceptance for {SubscriptionId} has no mailable UPN; confirmation email skipped.", subscription.AmpsubscriptionId);
                return;
            }

            var template = this.emailTemplateRepo.GetTemplateForStatus(EmailTemplateStatus);
            if (template == null || !template.IsActive || string.IsNullOrWhiteSpace(template.TemplateBody))
            {
                this.logger.LogWarning("No active '{Status}' email template; terms confirmation email skipped.", EmailTemplateStatus);
                return;
            }

            var body = template.TemplateBody
                .Replace("****SubscriptionName****", WebUtility.HtmlEncode(subscription.Name ?? string.Empty))
                .Replace("****SubscriptionId****", subscription.AmpsubscriptionId.ToString())
                .Replace("****AcceptedBy****", WebUtility.HtmlEncode(acceptance.AcceptedByUpn))
                .Replace("****AcceptedUtc****", acceptance.AcceptedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC")
                .Replace("****MicrosoftContractUrl****", this.AbsoluteUrl(acceptance.MicrosoftContractUrl))
                .Replace("****MicrosoftContractVersion****", WebUtility.HtmlEncode(DescribeVersion(acceptance.MicrosoftContractVersion)))
                .Replace("****PublisherAmendmentTitle****", WebUtility.HtmlEncode(acceptance.PublisherAmendmentTitle ?? string.Empty))
                .Replace("****PublisherAmendmentUrl****", this.AbsoluteUrl(acceptance.PublisherAmendmentUrl))
                .Replace("****PublisherAmendmentVersion****", WebUtility.HtmlEncode(DescribeVersion(acceptance.PublisherAmendmentVersion)));

            var subject = (template.Subject ?? "Terms accepted")
                .Replace("****SubscriptionName****", subscription.Name ?? string.Empty);

            // Same SMTP settings EmailHelper.FinalizeContentEmail reads; kept local so this service
            // does not need EmailHelper's four unrelated repositories.
            var content = new EmailContentModel
            {
                Subject = subject,
                Body = body,
                ToEmails = acceptance.AcceptedByUpn,
                CCEmails = template.Cc,
                BCCEmails = template.Bcc,
                FromEmail = this.configRepo.GetValueByName("SMTPFromEmail"),
                Password = this.configRepo.GetValueByName("SMTPPassword"),
                UserName = this.configRepo.GetValueByName("SMTPUserName"),
                SMTPHost = this.configRepo.GetValueByName("SMTPHost"),
                SSL = bool.TryParse(this.configRepo.GetValueByName("SMTPSslEnabled"), out var ssl) && ssl,
                Port = int.TryParse(this.configRepo.GetValueByName("SMTPPort"), out var port) ? port : 0,
            };

            this.emailService.SendEmail(content);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Terms confirmation email failed for {SubscriptionId} (acceptance already recorded).", subscription.AmpsubscriptionId);
        }
    }

    /// <summary>A site-relative document path becomes absolute using CustomerSiteBaseUrl, so it works in an email.</summary>
    private string AbsoluteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith('/'))
        {
            return url ?? string.Empty;
        }

        var baseUrl = this.saasConfig?.CustomerSiteBaseUrl?.TrimEnd('/');
        return string.IsNullOrEmpty(baseUrl) ? url : baseUrl + url;
    }

    private static string DescribeVersion(string version) =>
        string.IsNullOrWhiteSpace(version) ? "(unversioned)" : "v" + version;

    private static string FirstNonBlank(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
}
