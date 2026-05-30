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

        // Step 4 gating: region fan-out complete + consent done. Once unlocked, the step is
        // Complete only when every enrolled site has been granted; a Pending or Failed site
        // keeps it InProgress (the failed row stays actionable via its Grant button).
        var step4Unlocked = consent?.TenantRegionsFanOutCompleteUtc.HasValue == true
                          && consent?.RuntimeAppConsentedUtc.HasValue == true;
        if (!step4Unlocked)
        {
            vm.Step4 = StepState.Locked;
        }
        else if (!sites.Any())
        {
            vm.Step4 = StepState.NotStarted;
        }
        else if (sites.All(s => string.Equals(s.Status, "Granted", StringComparison.OrdinalIgnoreCase)))
        {
            vm.Step4 = StepState.Complete;
        }
        else
        {
            vm.Step4 = StepState.InProgress;
        }

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
    public Task<IActionResult> AddSite(Guid subscriptionId, string sharePointSiteUrl, CancellationToken ct)
        => this.RunAddSiteOrChallengeAsync(subscriptionId, sharePointSiteUrl, ct);

    /// <summary>
    /// POST wrapper for AddSite. Validating the URL against Graph needs a delegated token; if
    /// it can't be acquired silently the core throws <see cref="MicrosoftIdentityWebChallengeUserException"/>,
    /// which we hand off to the GET <see cref="ResumeAddSite"/> action for interactive consent
    /// (the entered URL is carried through on the query string), mirroring the grant flow.
    /// </summary>
    private async Task<IActionResult> RunAddSiteOrChallengeAsync(Guid subscriptionId, string sharePointSiteUrl, CancellationToken ct)
    {
        try
        {
            return await this.AddSiteCoreAsync(subscriptionId, sharePointSiteUrl, ct).ConfigureAwait(false);
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            return this.RedirectToAction(nameof(ResumeAddSite), new { subscriptionId, sharePointSiteUrl });
        }
    }

    /// <summary>
    /// Post-interactive-consent resume target for AddSite. Reached only via redirect from
    /// <see cref="RunAddSiteOrChallengeAsync"/>; [AuthorizeForScopes] drives the sign-in and
    /// returns the user here with the site URL intact, by which point the delegated token is
    /// cached and validation can complete.
    /// </summary>
    [HttpGet("/Setup/{subscriptionId:guid}/AddSite/Resume")]
    [AuthorizeForScopes(Scopes = new[] { "https://graph.microsoft.com/Sites.FullControl.All" })]
    public Task<IActionResult> ResumeAddSite(Guid subscriptionId, string sharePointSiteUrl, CancellationToken ct)
        => this.AddSiteCoreAsync(subscriptionId, sharePointSiteUrl, ct);

    /// <summary>
    /// Validates the supplied URL against Microsoft Graph and, only if the site actually
    /// exists, enrolls it -- storing the resolved Graph site id so the later Grant step
    /// doesn't have to resolve it again. A URL that doesn't resolve is rejected with an error
    /// and no row is created.
    /// </summary>
    private async Task<IActionResult> AddSiteCoreAsync(Guid subscriptionId, string sharePointSiteUrl, CancellationToken ct)
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

        // Delegated Graph token to verify the site exists. A silent-acquisition failure throws
        // MicrosoftIdentityWebChallengeUserException, which we let propagate so the POST wrapper /
        // resume action can prompt for interactive consent; other token errors are non-fatal here.
        string token;
        try
        {
            token = await this.tokenAcquisition
                .GetAccessTokenForUserAsync(SitePermissionScopes)
                .ConfigureAwait(false);
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(HttpUtility.HtmlEncode($"AddSite token acquisition failed for {subscriptionId}: {ex.Message}"));
            this.TempData["FlashMessage"] = "Couldn't verify the site right now. Please try again.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        // Resolve the URL to a Graph site id. A non-existent site 404s here -> reject without
        // creating a row, so only real sites get enrolled.
        string graphSiteId;
        try
        {
            graphSiteId = await this.sitePermissionService
                .ResolveGraphSiteIdAsync(canonicalUrl, token, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(HttpUtility.HtmlEncode($"AddSite site lookup failed for {subscriptionId} {canonicalUrl}: {ex.Message}"));
            this.TempData["FlashMessage"] = $"Couldn't find a SharePoint site at '{canonicalUrl}'. Check the URL and try again.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        var site = new Marketplace.SaaS.Accelerator.DataAccess.Entities.SubscriptionSite
        {
            AmpSubscriptionId = subscriptionId,
            SharePointSiteUrl = canonicalUrl,
            GraphSiteId = graphSiteId,
            Status = "Pending",
            CreatedUtc = DateTime.UtcNow,
        };
        this.siteRepo.Save(site);

        this.logger.Info(HttpUtility.HtmlEncode($"Setup site enrolled: {subscriptionId} {canonicalUrl} by {this.CurrentUserEmailAddress}"));
        this.TempData["FlashMessage"] = $"Site '{canonicalUrl}' verified and enrolled. Click Grant access to grant permissions.";
        return this.RedirectToAction(nameof(Index), new { subscriptionId });
    }

    [HttpPost("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/Grant")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> GrantSiteAccess(Guid subscriptionId, int siteId, CancellationToken ct)
        => this.RunTransitionOrChallengeAsync(subscriptionId, siteId, SiteTransition.Grant, ct);

    [HttpPost("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/SwitchToRead")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SwitchSiteToRead(Guid subscriptionId, int siteId, CancellationToken ct)
        => this.RunTransitionOrChallengeAsync(subscriptionId, siteId, SiteTransition.SwitchToRead, ct);

    [HttpPost("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/SwitchToManage")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SwitchSiteToManage(Guid subscriptionId, int siteId, CancellationToken ct)
        => this.RunTransitionOrChallengeAsync(subscriptionId, siteId, SiteTransition.SwitchToManage, ct);

    private enum SiteTransition { Grant, SwitchToRead, SwitchToManage }

    /// <summary>
    /// POST entry-point wrapper for the three site role transitions. Runs the transition;
    /// if acquiring the delegated Graph token requires interactive sign-in (the user's
    /// token isn't in cache -- e.g. after an app recycle, a new session, or because
    /// Sites.FullControl.All was never delegated-consented), Microsoft.Identity.Web throws
    /// <see cref="MicrosoftIdentityWebChallengeUserException"/>. A POST route can't be the
    /// post-sign-in redirect target (the return leg from the identity provider is always a
    /// GET), so we hand off to the GET <see cref="ResumeSiteTransition"/> action, which is
    /// decorated with [AuthorizeForScopes] and drives the interactive consent.
    /// </summary>
    private async Task<IActionResult> RunTransitionOrChallengeAsync(Guid subscriptionId, int siteId, SiteTransition transition, CancellationToken ct)
    {
        try
        {
            return await this.TransitionSiteRoleAsync(subscriptionId, siteId, transition, ct).ConfigureAwait(false);
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            return this.RedirectToAction(nameof(ResumeSiteTransition), new { subscriptionId, siteId, transition = transition.ToString() });
        }
    }

    /// <summary>
    /// Post-interactive-consent resume target for a site role transition. Reached only via
    /// redirect from <see cref="RunTransitionOrChallengeAsync"/> when the delegated token
    /// could not be acquired silently. [AuthorizeForScopes] catches the
    /// <see cref="MicrosoftIdentityWebChallengeUserException"/> that the transition throws,
    /// performs the interactive sign-in for Sites.FullControl.All, and redirects the user
    /// back here -- by which point the delegated token is cached and the transition runs to
    /// completion. Access is still gated by the purchaser-email ownership check inside the
    /// transition, so the GET (no antiforgery token) cannot grant access to an arbitrary site.
    /// </summary>
    [HttpGet("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/Resume")]
    [AuthorizeForScopes(Scopes = new[] { "https://graph.microsoft.com/Sites.FullControl.All" })]
    public Task<IActionResult> ResumeSiteTransition(Guid subscriptionId, int siteId, string transition, CancellationToken ct)
    {
        if (!Enum.TryParse<SiteTransition>(transition, ignoreCase: true, out var parsed))
        {
            return Task.FromResult<IActionResult>(this.RedirectToAction(nameof(Index), new { subscriptionId }));
        }

        return this.TransitionSiteRoleAsync(subscriptionId, siteId, parsed, ct);
    }

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
            // caches the token from sign-in and silently refreshes when needed. If it can't
            // be acquired silently (cache empty after an app recycle / new session, or
            // Sites.FullControl.All not yet delegated-consented), this throws
            // MicrosoftIdentityWebChallengeUserException. That case is caught below and
            // re-thrown unchanged so the caller can drive an interactive consent prompt --
            // we must NOT mark the site row Failed for it, because the Graph state is fine;
            // it's the user's token that needs refreshing.
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
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            // The delegated token needs an interactive sign-in. Propagate so the POST
            // wrapper / [AuthorizeForScopes] resume action can prompt the user. The Graph
            // state is unchanged, so leave the site row as-is (do NOT mark it Failed).
            throw;
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
