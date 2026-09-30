using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;
using Marketplace.SaaS.Accelerator.Services.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Marketplace.SaaS.Accelerator.Services.Test;

[TestClass]
public class TermsAcceptanceServiceTest
{
    private static readonly Guid SubscriptionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private Dictionary<string, string> config;
    private List<SubscriptionTermsAcceptance> acceptances;
    private List<SubscriptionAuditLogs> auditRows;
    private List<EmailContentModel> sentEmails;
    private EmailTemplate template;

    private Mock<ISubscriptionTermsAcceptanceRepository> acceptanceRepo;
    private Mock<ISubscriptionsRepository> subscriptionsRepo;
    private Mock<ISubscriptionLogRepository> logRepo;
    private Mock<IUsersRepository> usersRepo;
    private Mock<IApplicationConfigRepository> configRepo;
    private Mock<IEmailTemplateRepository> templateRepo;
    private Mock<IEmailService> emailService;
    private Mock<IApplicationLogRepository> appLogRepo;

    [TestInitialize]
    public void Initialize()
    {
        this.config = new Dictionary<string, string>
        {
            [TermsAcceptanceService.RequiredKey] = "true",
            [TermsAcceptanceService.MicrosoftContractUrlKey] = "https://go.microsoft.com/fwlink/?linkid=2041178",
            [TermsAcceptanceService.MicrosoftContractVersionKey] = "",
            [TermsAcceptanceService.PublisherAmendmentTitleKey] = "SP Additions Ltd amendment",
            [TermsAcceptanceService.PublisherAmendmentUrlKey] = "/legal/amendment-v1.html",
            [TermsAcceptanceService.PublisherAmendmentVersionKey] = "1.0",
            ["SMTPFromEmail"] = "noreply@example.com",
            ["SMTPHost"] = "smtp.example.com",
            ["SMTPPort"] = "587",
            ["SMTPSslEnabled"] = "true",
        };
        this.acceptances = new List<SubscriptionTermsAcceptance>();
        this.auditRows = new List<SubscriptionAuditLogs>();
        this.sentEmails = new List<EmailContentModel>();
        this.template = new EmailTemplate
        {
            Status = TermsAcceptanceService.EmailTemplateStatus,
            IsActive = true,
            Subject = "Terms for ****SubscriptionName****",
            TemplateBody = "<p>****AcceptedBy**** ****MicrosoftContractUrl**** ****PublisherAmendmentUrl**** ****PublisherAmendmentVersion****</p>",
            Bcc = "records@publisher.example",
        };

        this.acceptanceRepo = new Mock<ISubscriptionTermsAcceptanceRepository>();
        this.acceptanceRepo.Setup(x => x.GetLatestByAmpSubscriptionId(SubscriptionId))
            .Returns(() => this.acceptances.OrderByDescending(a => a.AcceptedUtc).FirstOrDefault());
        this.acceptanceRepo.Setup(x => x.Add(It.IsAny<SubscriptionTermsAcceptance>()))
            .Callback<SubscriptionTermsAcceptance>(a => { a.Id = this.acceptances.Count + 1; this.acceptances.Add(a); })
            .Returns(() => this.acceptances.Count);

        this.subscriptionsRepo = new Mock<ISubscriptionsRepository>();
        this.subscriptionsRepo.Setup(x => x.GetById(SubscriptionId, It.IsAny<bool>()))
            .Returns(new Subscriptions
            {
                Id = 42,
                AmpsubscriptionId = SubscriptionId,
                Name = "Contoso RnU",
                PurchaserTenantId = TenantId,
                SubscriptionStatus = "Subscribed",
            });

        this.logRepo = new Mock<ISubscriptionLogRepository>();
        this.logRepo.Setup(x => x.Save(It.IsAny<SubscriptionAuditLogs>()))
            .Callback<SubscriptionAuditLogs>(r => this.auditRows.Add(r))
            .Returns(1);

        this.usersRepo = new Mock<IUsersRepository>();
        this.usersRepo.Setup(x => x.GetPartnerDetailFromEmail("admin@contoso.com"))
            .Returns(new Users { UserId = 7, EmailAddress = "admin@contoso.com" });

        this.configRepo = new Mock<IApplicationConfigRepository>();
        this.configRepo.Setup(x => x.GetValueByName(It.IsAny<string>()))
            .Returns<string>(name => this.config.TryGetValue(name, out var v) ? v : null);

        this.templateRepo = new Mock<IEmailTemplateRepository>();
        this.templateRepo.Setup(x => x.GetTemplateForStatus(TermsAcceptanceService.EmailTemplateStatus))
            .Returns(() => this.template);

        this.emailService = new Mock<IEmailService>();
        this.emailService.Setup(x => x.SendEmail(It.IsAny<EmailContentModel>()))
            .Callback<EmailContentModel>(m => this.sentEmails.Add(m));

        this.appLogRepo = new Mock<IApplicationLogRepository>();
        this.appLogRepo.Setup(x => x.AddLog(It.IsAny<ApplicationLog>())).ReturnsAsync(1);
    }

    private TermsAcceptanceService Build() => new TermsAcceptanceService(
        this.acceptanceRepo.Object,
        this.subscriptionsRepo.Object,
        this.logRepo.Object,
        this.usersRepo.Object,
        this.configRepo.Object,
        this.templateRepo.Object,
        this.emailService.Object,
        this.appLogRepo.Object,
        new SaaSApiClientConfiguration { CustomerSiteBaseUrl = "https://portal.example.com/" },
        NullLogger<TermsAcceptanceService>.Instance);

    private static TermsAcceptanceRequest FullRequest() => new()
    {
        MicrosoftContractAccepted = true,
        PublisherAmendmentAccepted = true,
        AcceptedByUpn = "admin@contoso.com",
        AcceptedByObjectId = "oid-1",
        AcceptedByDisplayName = "Contoso Admin",
        IpAddress = "203.0.113.5",
        UserAgent = "UnitTest/1.0",
    };

    // ---- Gate evaluation ----

    [TestMethod]
    public void NoAcceptance_GateBlocks()
    {
        var status = this.Build().GetStatus(SubscriptionId);

        Assert.IsTrue(status.Required);
        Assert.IsTrue(status.IsConfigured);
        Assert.IsFalse(status.Accepted);
        Assert.IsFalse(status.IsSatisfied);
        Assert.IsFalse(status.IsMisconfigured);
    }

    [TestMethod]
    public void ExistingAcceptance_GateSatisfied()
    {
        this.acceptances.Add(new SubscriptionTermsAcceptance { Id = 1, AmpSubscriptionId = SubscriptionId, AcceptedUtc = DateTime.UtcNow.AddDays(-1) });

        var status = this.Build().GetStatus(SubscriptionId);

        Assert.IsTrue(status.Accepted);
        Assert.IsTrue(status.IsSatisfied);
    }

    [TestMethod]
    public void VersionBumpAfterAcceptance_DoesNotReprompt()
    {
        // Agreed 2026-09-30: acceptance is once per subscription; the versions on the row are a record,
        // not a re-consent trigger.
        this.acceptances.Add(new SubscriptionTermsAcceptance
        {
            Id = 1,
            AmpSubscriptionId = SubscriptionId,
            AcceptedUtc = DateTime.UtcNow.AddDays(-30),
            PublisherAmendmentVersion = "1.0",
        });
        this.config[TermsAcceptanceService.PublisherAmendmentVersionKey] = "2.0";

        var status = this.Build().GetStatus(SubscriptionId);

        Assert.IsTrue(status.IsSatisfied);
    }

    [TestMethod]
    public void KillSwitchOff_GateSatisfiedWithoutAcceptance()
    {
        this.config[TermsAcceptanceService.RequiredKey] = "false";

        var status = this.Build().GetStatus(SubscriptionId);

        Assert.IsFalse(status.Required);
        Assert.IsTrue(status.IsSatisfied);
    }

    [TestMethod]
    public void MissingKillSwitchRow_DefaultsToRequired()
    {
        this.config.Remove(TermsAcceptanceService.RequiredKey);

        Assert.IsTrue(this.Build().GetStatus(SubscriptionId).Required);
    }

    [TestMethod]
    public void MissingDocumentUrl_FailsClosed()
    {
        this.config[TermsAcceptanceService.PublisherAmendmentUrlKey] = "";

        var status = this.Build().GetStatus(SubscriptionId);

        Assert.IsFalse(status.IsConfigured);
        Assert.IsTrue(status.IsMisconfigured);
        Assert.IsFalse(status.IsSatisfied);
    }

    [TestMethod]
    public void MissingTitle_FallsBackToDefault()
    {
        this.config.Remove(TermsAcceptanceService.PublisherAmendmentTitleKey);

        var status = this.Build().GetConfiguration();

        Assert.IsFalse(string.IsNullOrWhiteSpace(status.PublisherAmendment.Title));
        Assert.IsFalse(string.IsNullOrWhiteSpace(status.MicrosoftContract.Title));
    }

    // ---- Recording ----

    [TestMethod]
    public void Record_PersistsWhoWhenAndWhichVersions()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = this.Build().Record(SubscriptionId, FullRequest());

        Assert.IsTrue(result.Success, result.Error);
        var row = this.acceptances.Single();
        Assert.AreEqual(SubscriptionId, row.AmpSubscriptionId);
        Assert.AreEqual(TenantId, row.TenantId);
        Assert.IsTrue(row.AcceptedUtc >= before);
        Assert.AreEqual("admin@contoso.com", row.AcceptedByUpn);
        Assert.AreEqual("oid-1", row.AcceptedByObjectId);
        Assert.AreEqual("Contoso Admin", row.AcceptedByDisplayName);
        Assert.IsTrue(row.MicrosoftContractAccepted);
        Assert.IsTrue(row.PublisherAmendmentAccepted);
        Assert.AreEqual("https://go.microsoft.com/fwlink/?linkid=2041178", row.MicrosoftContractUrl);
        Assert.AreEqual("/legal/amendment-v1.html", row.PublisherAmendmentUrl);
        Assert.AreEqual("1.0", row.PublisherAmendmentVersion);
        Assert.AreEqual("SP Additions Ltd amendment", row.PublisherAmendmentTitle);
        Assert.AreEqual("203.0.113.5", row.IpAddress);
        Assert.AreEqual("UnitTest/1.0", row.UserAgent);
        Assert.AreEqual("Setup", row.Source);
    }

    [TestMethod]
    public void Record_WritesSubscriptionAuditLog()
    {
        this.Build().Record(SubscriptionId, FullRequest());

        var log = this.auditRows.Single();
        Assert.AreEqual(SubscriptionLogAttributes.TermsAccepted.ToString(), log.Attribute);
        Assert.AreEqual(42, log.SubscriptionId);
        Assert.AreEqual(7, log.CreateBy);
        StringAssert.Contains(log.NewValue, "admin@contoso.com");
        StringAssert.Contains(log.NewValue, "v1.0");
    }

    [TestMethod]
    public void Record_SendsConfirmationToAccepterWithAbsoluteLinks()
    {
        this.Build().Record(SubscriptionId, FullRequest());

        var mail = this.sentEmails.Single();
        Assert.AreEqual("admin@contoso.com", mail.ToEmails);
        Assert.AreEqual("records@publisher.example", mail.BCCEmails);
        Assert.AreEqual("Terms for Contoso RnU", mail.Subject);
        StringAssert.Contains(mail.Body, "https://portal.example.com/legal/amendment-v1.html");
        StringAssert.Contains(mail.Body, "https://go.microsoft.com/fwlink/?linkid=2041178");
        StringAssert.Contains(mail.Body, "v1.0");
        Assert.AreEqual("smtp.example.com", mail.SMTPHost);
        Assert.AreEqual(587, mail.Port);
        Assert.IsTrue(mail.SSL);
    }

    [TestMethod]
    public void Record_EmailDisabled_StillRecordsAcceptance()
    {
        this.config[TermsAcceptanceService.EmailEnabledKey] = "false";

        var result = this.Build().Record(SubscriptionId, FullRequest());

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, this.acceptances.Count);
        Assert.AreEqual(0, this.sentEmails.Count);
    }

    [TestMethod]
    public void Record_EmailFailure_DoesNotFailAcceptance()
    {
        this.emailService.Setup(x => x.SendEmail(It.IsAny<EmailContentModel>())).Throws(new InvalidOperationException("smtp down"));

        var result = this.Build().Record(SubscriptionId, FullRequest());

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, this.acceptances.Count);
    }

    [TestMethod]
    public void Record_NoTemplate_SkipsEmailQuietly()
    {
        this.template = null;

        var result = this.Build().Record(SubscriptionId, FullRequest());

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, this.sentEmails.Count);
    }

    [TestMethod]
    public void Record_OneBoxUnticked_Rejected()
    {
        var request = FullRequest();
        request.PublisherAmendmentAccepted = false;

        var result = this.Build().Record(SubscriptionId, request);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, this.acceptances.Count);
        Assert.AreEqual(0, this.sentEmails.Count);
    }

    [TestMethod]
    public void Record_Misconfigured_Rejected()
    {
        this.config[TermsAcceptanceService.MicrosoftContractUrlKey] = "";

        var result = this.Build().Record(SubscriptionId, FullRequest());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, this.acceptances.Count);
    }

    [TestMethod]
    public void Record_SecondSubmit_IsIdempotent()
    {
        var service = this.Build();
        service.Record(SubscriptionId, FullRequest());
        var second = service.Record(SubscriptionId, FullRequest());

        Assert.IsTrue(second.Success);
        Assert.AreEqual(1, this.acceptances.Count, "a double-submit must not create a second legal record");
        Assert.AreEqual(1, this.sentEmails.Count);
    }

    [TestMethod]
    public void Record_UnknownSubscription_Rejected()
    {
        var other = Guid.NewGuid();
        this.acceptanceRepo.Setup(x => x.GetLatestByAmpSubscriptionId(other)).Returns((SubscriptionTermsAcceptance)null);

        var result = this.Build().Record(other, FullRequest());

        Assert.IsFalse(result.Success);
    }
}
