// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Marketplace.SaaS.Accelerator.CustomerSite.Models;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

namespace Marketplace.SaaS.Accelerator.CustomerSite.Controllers;

/// <summary>
/// Persistent post-acceptance setup page for Read and Understood.
/// Steps: 1 subscription active (auto) - 2 database region - 3 tenant consent - 4 sites.
/// </summary>
public class SetupController : BaseController
{
    private readonly ISubscriptionsRepository subscriptionRepo;
    private readonly ISubscriptionTenantConsentRepository consentRepo;
    private readonly ISubscriptionSiteRepository siteRepo;
    private readonly IAzureRegionService regionService;
    private readonly ITenantAdminConsentService consentService;
    private readonly ISitePermissionService sitePermissionService;
    private readonly ITokenAcquisition tokenAcquisition;
    private readonly SaaSApiClientConfiguration config;
    private readonly SaaSClientLogger<SetupController> logger;

    /// <summary>Graph scopes requested per-call. Must be a subset of what was admin-consented at sign-in.</summary>
    private static readonly string[] SitePermissionScopes = new[]
    {
        "https://graph.microsoft.com/Sites.FullControl.All",
    };

    public SetupController(
        IAppVersionService appVersionService,
        ISubscriptionsRepository subscriptionRepo,
        ISubscriptionTenantConsentRepository consentRepo,
        ISubscriptionSiteRepository siteRepo,
        IAzureRegionService regionService,
        ITenantAdminConsentService consentService,
        ISitePermissionService sitePermissionService,
        ITokenAcquisition tokenAcquisition,
        SaaSApiClientConfiguration config,
        SaaSClientLogger<SetupController> logger) : base(appVersionService)
    {
        this.subscriptionRepo = subscriptionRepo;
        this.consentRepo = consentRepo;
        this.siteRepo = siteRepo;
        this.regionService = regionService;
        this.consentService = consentService;
        this.sitePermissionService = sitePermissionService;
        this.tokenAcquisition = tokenAcquisition;
        this.config = config;
        this.logger = logger;
    }

    [HttpGet("/Setup/{subscriptionId:guid}")]
    public async Task<IActionResult> Index(Guid subscriptionId, CancellationToken ct)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        var subscription = this.subscriptionRepo.GetById(subscriptionId);
        if (subscription == null
            || !string.Equals(subscription.PurchaserEmail, this.CurrentUserEmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            this.logger.LogError(HttpUtility.HtmlEncode($"Setup access denied for {subscriptionId} by {this.CurrentUserEmailAddress}"));
            return this.RedirectToAction("Index", "Home");
        }

        var tenantId = subscription.PurchaserTenantId ?? Guid.Empty;
        var consent = this.consentRepo.GetByAmpSubscriptionId(subscriptionId);
        var sites = this.siteRepo.ListBySubscription(subscriptionId).ToList();

        var vm = new SetupViewModel
        {
            AmpSubscriptionId = subscriptionId,
            SubscriptionName = subscription.Name,
            PlanId = subscription.AmpplanId,
            TenantId = tenantId,
            Step1 = StepState.Complete,
        };

        // Step 2: region
        if (consent?.AzureRegion != null)
        {
            vm.Step2 = consent.TenantRegionsFanOutCompleteUtc.HasValue ? StepState.Complete : StepState.InProgress;
            vm.RegionPicker = new RegionPickerViewModel
            {
                Mode = "saved",
                SelectedRegion = consent.AzureRegion,
                SelectedRegionFriendly = consent.AzureRegion,
                FanOutComplete = consent.TenantRegionsFanOutCompleteUtc.HasValue,
                SelectedUtc = consent.AzureRegionSelectedUtc,
                SelectedByUpn = consent.AzureRegionSelectedByUpn,
            };
        }
        else
        {
            vm.Step2 = StepState.NotStarted;
            if (tenantId != Guid.Empty)
            {
                var lookup = await this.regionService.GetTenantRegionAsync(tenantId, ct).ConfigureAwait(false);
                vm.RegionPicker = new RegionPickerViewModel
                {
                    Mode = lookup.IsFallback
                        ? "fallback"
                        : (lookup.AzRegion != null && lookup.AzRegion != "?" ? "detected" : "picker"),
                    SelectedRegion = lookup.AzRegion != "?" ? lookup.AzRegion : null,
                    SelectedRegionFriendly = lookup.AzureRegionSelectors
                        ?.FirstOrDefault(s => s.Key == lookup.AzRegion)?.Text
                        ?? lookup.AzRegion,
                    Selectors = lookup.AzureRegionSelectors ?? new(),
                    ErrorMessage = lookup.Error,
                };
            }
            else
            {
                vm.RegionPicker = new RegionPickerViewModel
                {
                    Mode = "fallback",
                    ErrorMessage = "Subscription has no purchaser tenant id; cannot query Function1.",
                };
            }
        }

        // Step 3 gating: region row persisted (fan-out in flight is OK)
        var regionSelected = consent?.AzureRegion != null;
        if (!regionSelected)
        {
            vm.Step3 = StepState.Locked;
        }
        else
        {
            vm.Step3 = consent.RuntimeAppConsentedUtc.HasValue ? StepState.Complete : StepState.NotStarted;
        }
        vm.Consent = new ConsentStepViewModel
        {
            Granted = consent?.RuntimeAppConsentedUtc.HasValue ?? false,
            GrantedUtc = consent?.RuntimeAppConsentedUtc,
            GrantedByUpn = consent?.ConsentedByUpn,
        };

        // Step 4 gating: region fan-out complete + consent done
        var step4Unlocked = consent?.TenantRegionsFanOutCompleteUtc.HasValue == true
                          && consent?.RuntimeAppConsentedUtc.HasValue == true;
        vm.Step4 = step4Unlocked
            ? (sites.Any() ? StepState.InProgress : StepState.NotStarted)
            : StepState.Locked;

        vm.Sites = sites.Select(s => new SiteRowViewModel
        {
            Id = s.Id,
            SharePointSiteUrl = s.SharePointSiteUrl,
            Status = s.Status,
            CurrentRole = s.CurrentRole,
            GrantedUtc = s.GrantedUtc,
            FailureReason = s.FailureReason,
            CanDowngrade = s.CurrentRole == "manage" && s.GrantedUtc.HasValue,
            CanReelevate = s.CurrentRole == "read",
        }).ToList();

        if (this.TempData["FlashMessage"] is string flash)
        {
            vm.FlashMessage = flash;
            vm.FlashIsError = this.TempData["FlashIsError"] is bool b && b;
        }

        return this.View(vm);
    }

    [HttpPost("/Setup/{subscriptionId:guid}/Region")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Region(Guid subscriptionId, string azureRegion, CancellationToken ct)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        var subscription = this.subscriptionRepo.GetById(subscriptionId);
        if (subscription == null
            || !string.Equals(subscription.PurchaserEmail, this.CurrentUserEmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (string.IsNullOrWhiteSpace(azureRegion))
        {
            this.TempData["FlashMessage"] = "Please choose a region.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        var tenantId = subscription.PurchaserTenantId ?? Guid.Empty;
        if (tenantId == Guid.Empty)
        {
            this.TempData["FlashMessage"] = "This subscription has no tenant id; contact support.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        // Validate against the selector list (refuse arbitrary values).
        var lookup = await this.regionService.GetTenantRegionAsync(tenantId, ct).ConfigureAwait(false);
        if (lookup.AzureRegionSelectors == null
            || !lookup.AzureRegionSelectors.Any(s => string.Equals(s.Key, azureRegion, StringComparison.OrdinalIgnoreCase)))
        {
            this.TempData["FlashMessage"] = $"'{azureRegion}' is not in the allowed region list.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        try
        {
            await this.regionService.SaveRegionAndEnqueueFanOutAsync(
                subscriptionId,
                tenantId,
                azureRegion,
                this.CurrentUserEmailAddress,
                ct).ConfigureAwait(false);
            this.TempData["FlashMessage"] = "Region saved. Propagating to all regions...";
        }
        catch (Exception ex)
        {
            this.logger.LogError($"Region save failed for {subscriptionId}: {ex.Message}");
            this.TempData["FlashMessage"] = "Could not save your region. Please try again.";
            this.TempData["FlashIsError"] = true;
        }

        return this.RedirectToAction(nameof(Index), new { subscriptionId });
    }

    [HttpGet("/Setup/{subscriptionId:guid}/Consent")]
    public IActionResult Consent(Guid subscriptionId)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        var subscription = this.subscriptionRepo.GetById(subscriptionId);
        if (subscription == null
            || !string.Equals(subscription.PurchaserEmail, this.CurrentUserEmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            return this.RedirectToAction("Index", "Home");
        }

        var tenantId = subscription.PurchaserTenantId ?? Guid.Empty;
        if (tenantId == Guid.Empty)
        {
            this.TempData["FlashMessage"] = "This subscription has no tenant id; contact support.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        var callbackUri = $"{this.Request.Scheme}://{this.Request.Host}/api/setup/consent-callback";
        string url;
        try
        {
            url = this.consentService.BuildConsentUrl(tenantId, subscriptionId, callbackUri);
        }
        catch (InvalidOperationException ex)
        {
            this.logger.LogError($"BuildConsentUrl failed: {ex.Message}");
            this.TempData["FlashMessage"] = "Tenant consent is not configured on this Accelerator. Contact support.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        return this.Redirect(url);
    }

    [HttpGet("/api/setup/consent-callback")]
    public async Task<IActionResult> ConsentCallback(
        string state,
        string admin_consent,
        string tenant,
        string error,
        string error_description,
        CancellationToken ct)
    {
        var subscriptionId = this.consentService.ValidateCallbackState(state);
        if (subscriptionId == null)
        {
            this.logger.LogError("Consent callback received invalid or expired state");
            return this.RedirectToAction("Index", "Home");
        }

        var sub = this.subscriptionRepo.GetById(subscriptionId.Value);
        if (sub == null)
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (!string.IsNullOrEmpty(error))
        {
            this.logger.LogError($"Consent callback error for {subscriptionId}: {error} - {error_description}");
            this.TempData["FlashMessage"] = $"Consent was not granted: {error}";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId = subscriptionId.Value });
        }

        var granted = string.Equals(admin_consent, "True", StringComparison.OrdinalIgnoreCase);
        if (!granted)
        {
            this.TempData["FlashMessage"] = "Consent was not granted.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId = subscriptionId.Value });
        }

        await this.consentService.RecordConsentAsync(
            subscriptionId.Value,
            this.CurrentUserEmailAddress,
            objectId: null,
            ct).ConfigureAwait(false);

        this.TempData["FlashMessage"] = "Tenant consent recorded.";
        return this.RedirectToAction(nameof(Index), new { subscriptionId = subscriptionId.Value });
    }

    [HttpGet("/Setup/{subscriptionId:guid}/Status.json")]
    public IActionResult Status(Guid subscriptionId)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.Unauthorized();
        }

        var subscription = this.subscriptionRepo.GetById(subscriptionId);
        if (subscription == null
            || !string.Equals(subscription.PurchaserEmail, this.CurrentUserEmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            return this.NotFound();
        }

        var consent = this.consentRepo.GetByAmpSubscriptionId(subscriptionId);
        var sites = this.siteRepo.ListBySubscription(subscriptionId);
        return this.Json(new
        {
            regionSelected = consent?.AzureRegion != null,
            regionFanOutComplete = consent?.TenantRegionsFanOutCompleteUtc.HasValue ?? false,
            consented = consent?.RuntimeAppConsentedUtc.HasValue ?? false,
            sites = sites.Select(s => new
            {
                id = s.Id,
                url = s.SharePointSiteUrl,
                status = s.Status,
                role = s.CurrentRole,
            }),
        });
    }

    [HttpPost("/Setup/{subscriptionId:guid}/AddSite")]
    [ValidateAntiForgeryToken]
    public IActionResult AddSite(Guid subscriptionId, string sharePointSiteUrl)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        var subscription = this.subscriptionRepo.GetById(subscriptionId);
        if (subscription == null
            || !string.Equals(subscription.PurchaserEmail, this.CurrentUserEmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (string.IsNullOrWhiteSpace(sharePointSiteUrl))
        {
            this.TempData["FlashMessage"] = "Please enter a SharePoint site URL.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        if (!Uri.TryCreate(sharePointSiteUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            this.TempData["FlashMessage"] = "Site URL must be an absolute HTTP/HTTPS URL.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        var canonicalUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');

        // Refuse duplicate enrollment of the same URL on the same subscription.
        if (this.siteRepo.ListBySubscription(subscriptionId)
            .Any(s => string.Equals(s.SharePointSiteUrl, canonicalUrl, StringComparison.OrdinalIgnoreCase)))
        {
            this.TempData["FlashMessage"] = $"Site '{canonicalUrl}' is already enrolled.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        var site = new Marketplace.SaaS.Accelerator.DataAccess.Entities.SubscriptionSite
        {
            AmpSubscriptionId = subscriptionId,
            SharePointSiteUrl = canonicalUrl,
            Status = "Pending",
            CreatedUtc = DateTime.UtcNow,
        };
        this.siteRepo.Save(site);

        this.logger.Info(HttpUtility.HtmlEncode($"Setup site enrolled: {subscriptionId} {canonicalUrl} by {this.CurrentUserEmailAddress}"));
        this.TempData["FlashMessage"] = $"Site '{canonicalUrl}' enrolled and queued for permission grant.";
        return this.RedirectToAction(nameof(Index), new { subscriptionId });
    }

    [HttpPost("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/Grant")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> GrantSiteAccess(Guid subscriptionId, int siteId, CancellationToken ct)
        => this.TransitionSiteRoleAsync(subscriptionId, siteId, SiteTransition.Grant, ct);

    [HttpPost("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/SwitchToRead")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SwitchSiteToRead(Guid subscriptionId, int siteId, CancellationToken ct)
        => this.TransitionSiteRoleAsync(subscriptionId, siteId, SiteTransition.SwitchToRead, ct);

    [HttpPost("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/SwitchToManage")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SwitchSiteToManage(Guid subscriptionId, int siteId, CancellationToken ct)
        => this.TransitionSiteRoleAsync(subscriptionId, siteId, SiteTransition.SwitchToManage, ct);

    private enum SiteTransition { Grant, SwitchToRead, SwitchToManage }

    /// <summary>
    /// Common path for Grant / SwitchToRead / SwitchToManage. Acquires a Graph access
    /// token for the runtime app in the customer's tenant, calls
    /// <see cref="ISitePermissionService"/> to perform the per-site Graph operation,
    /// and only on success updates the local SubscriptionSite row.
    /// </summary>
    private async Task<IActionResult> TransitionSiteRoleAsync(Guid subscriptionId, int siteId, SiteTransition transition, CancellationToken ct)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        var subscription = this.subscriptionRepo.GetById(subscriptionId);
        if (subscription == null
            || !string.Equals(subscription.PurchaserEmail, this.CurrentUserEmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            return this.RedirectToAction("Index", "Home");
        }

        var site = this.siteRepo.Get(siteId);
        if (site == null || site.AmpSubscriptionId != subscriptionId)
        {
            this.TempData["FlashMessage"] = "Site not found.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        try
        {
            // Delegated Graph token in the signed-in user's tenant. Microsoft.Identity.Web
            // caches the token from sign-in and silently refreshes when needed. If the user
            // hasn't consented to Sites.FullControl.All (or did once but a new permission has
            // since been added), this throws MsalUiRequiredException -- we surface that as a
            // re-consent prompt rather than a generic failure.
            var token = await this.tokenAcquisition
                .GetAccessTokenForUserAsync(SitePermissionScopes)
                .ConfigureAwait(false);

            var now = DateTime.UtcNow;

            switch (transition)
            {
                case SiteTransition.Grant:
                    // First-time grant: resolve the site URL to a Graph site id if we don't
                    // have one yet, then create the per-site permission at Manage.
                    if (string.IsNullOrWhiteSpace(site.GraphSiteId))
                    {
                        site.GraphSiteId = await this.sitePermissionService
                            .ResolveGraphSiteIdAsync(site.SharePointSiteUrl, token, ct)
                            .ConfigureAwait(false);
                    }
                    var grant = await this.sitePermissionService
                        .GrantManageAsync(site.GraphSiteId, token, ct)
                        .ConfigureAwait(false);
                    site.PermissionId = grant.PermissionId;
                    site.CurrentRole = "manage";
                    site.Status = "Granted";
                    site.GrantedUtc = now;
                    site.GrantedByUpn = this.CurrentUserEmailAddress;
                    site.FailureReason = null;
                    break;

                case SiteTransition.SwitchToRead:
                    await this.sitePermissionService.DowngradeToReadAsync(site, token, ct).ConfigureAwait(false);
                    site.CurrentRole = "read";
                    site.DowngradedUtc = now;
                    site.FailureReason = null;
                    break;

                case SiteTransition.SwitchToManage:
                    await this.sitePermissionService.ReelevateToManageAsync(site, token, ct).ConfigureAwait(false);
                    site.CurrentRole = "manage";
                    site.GrantedUtc = now;
                    site.GrantedByUpn = this.CurrentUserEmailAddress;
                    site.FailureReason = null;
                    break;
            }

            site.ModifiedUtc = now;
            this.siteRepo.Save(site);

            this.logger.Info(HttpUtility.HtmlEncode(
                $"Setup site role transition: subscription={subscriptionId} site={siteId} transition={transition} by={this.CurrentUserEmailAddress}"));

            this.TempData["FlashMessage"] = transition == SiteTransition.SwitchToRead
                ? $"Site '{site.SharePointSiteUrl}' is now set to Read."
                : $"Site '{site.SharePointSiteUrl}' is now set to Manage.";
        }
        catch (Exception ex)
        {
            // Persist the failure to the row so the UI can show it; do NOT flip CurrentRole
            // since the underlying Graph state didn't change.
            site.Status = "Failed";
            site.FailureReason = ex.Message.Length > 2000 ? ex.Message.Substring(0, 2000) : ex.Message;
            site.ModifiedUtc = DateTime.UtcNow;
            this.siteRepo.Save(site);

            this.logger.LogError(HttpUtility.HtmlEncode(
                $"Setup site transition failed: subscription={subscriptionId} site={siteId} transition={transition}: {ex.Message}"));

            this.TempData["FlashMessage"] = $"Could not change permission on '{site.SharePointSiteUrl}'. See the site row for details.";
            this.TempData["FlashIsError"] = true;
        }

        return this.RedirectToAction(nameof(Index), new { subscriptionId });
    }
}
