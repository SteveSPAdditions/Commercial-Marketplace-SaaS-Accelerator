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

    /// <summary>
    /// Graph scopes requested per-call during Setup. These are acquired just-in-time via
    /// incremental consent -- they do NOT need to be (and intentionally are not) consented at
    /// initial sign-in. When a token can't be acquired silently, the action throws
    /// MicrosoftIdentityWebChallengeUserException and the [AuthorizeForScopes]-decorated Resume
    /// action drives the interactive consent prompt at the point of use (Step 4).
    /// </summary>
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

    /// <summary>
    /// Signed-in user's home tenant id (the 'tid' claim), or <see cref="Guid.Empty"/> if absent.
    /// </summary>
    private Guid CurrentUserTenantId
    {
        get
        {
            var tid = this.User?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
                ?? this.User?.FindFirst("tid")?.Value;
            return Guid.TryParse(tid, out var t) ? t : Guid.Empty;
        }
    }

    /// <summary>
    /// Loads the subscription and authorizes the signed-in user against it. Access is granted to
    /// any user in the SAME TENANT as the subscription (<c>PurchaserTenantId</c>): the person who
    /// completes Setup (a tenant admin arriving from Azure -> SaaS) is frequently NOT the
    /// individual who purchased, so we authorize by tenant rather than by purchaser email.
    /// Returns false when the subscription is missing or belongs to a different tenant; the
    /// denial is logged. <paramref name="subscription"/> may be null on a false return.
    /// </summary>
    private bool TryAuthorizeSetup(Guid subscriptionId, out Marketplace.SaaS.Accelerator.DataAccess.Entities.Subscriptions subscription)
    {
        subscription = this.subscriptionRepo.GetById(subscriptionId);
        if (subscription == null)
        {
            this.logger.LogError(HttpUtility.HtmlEncode(
                $"Setup access denied: subscription {subscriptionId} not found (user {this.CurrentUserEmailAddress})"));
            return false;
        }

        var userTenantId = this.CurrentUserTenantId;
        if (userTenantId == Guid.Empty
            || subscription.PurchaserTenantId == null
            || subscription.PurchaserTenantId.Value != userTenantId)
        {
            this.logger.LogError(HttpUtility.HtmlEncode(
                $"Setup access denied for {subscriptionId}: user tenant '{userTenantId}' != subscription tenant '{subscription.PurchaserTenantId}' (user {this.CurrentUserEmailAddress})"));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Standard response when the signed-in user is not authorized for a subscription's setup:
    /// surfaces a message on the subscriptions list (which reads TempData["ErrorMsg"]) and
    /// redirects there, rather than silently bouncing to the landing page.
    /// </summary>
    private IActionResult SetupAccessDenied()
    {
        this.TempData["ErrorMsg"] = "You don't have access to that subscription's setup. It must be completed by a user signed in to the same Microsoft Entra tenant as the subscription.";
        return this.RedirectToAction("Subscriptions", "Home");
    }

    [HttpGet("/Setup/{subscriptionId:guid}")]
    public async Task<IActionResult> Index(Guid subscriptionId, CancellationToken ct)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (!this.TryAuthorizeSetup(subscriptionId, out var subscription))
        {
            return this.SetupAccessDenied();
        }

        var vm = await this.BuildSetupViewModelAsync(subscriptionId, subscription, ct).ConfigureAwait(false);

        if (this.TempData["FlashMessage"] is string flash)
        {
            vm.FlashMessage = flash;
            vm.FlashIsError = this.TempData["FlashIsError"] is bool b && b;
        }

        return this.View(vm);
    }

    /// <summary>
    /// Renders the setup checklist as a partial for inline embedding in the subscriptions-list
    /// accordion. Lazy-loaded via AJAX on first expand, and re-fetched by the client while a
    /// step is propagating (in place of the standalone page's full reload).
    /// </summary>
    [HttpGet("/Setup/{subscriptionId:guid}/Panel")]
    public async Task<IActionResult> Panel(Guid subscriptionId, CancellationToken ct)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.Unauthorized();
        }

        if (!this.TryAuthorizeSetup(subscriptionId, out var subscription))
        {
            return this.NotFound();
        }

        var vm = await this.BuildSetupViewModelAsync(subscriptionId, subscription, ct).ConfigureAwait(false);
        return this.SetupPanel(vm);
    }

    /// <summary>
    /// Builds the full <see cref="SetupViewModel"/> for a subscription: queries Function1 for the
    /// tenant region (the authority, self-healing the stored value), then derives each step's state
    /// from the persisted consent + site rows. Shared by the standalone page (<see cref="Index"/>),
    /// the inline <see cref="Panel"/>, and the AJAX action responses. Expensive (a network call and
    /// a possible DB write), so it runs once per subscription on demand -- never eagerly for a list.
    /// </summary>
    private async Task<SetupViewModel> BuildSetupViewModelAsync(
        Guid subscriptionId,
        Marketplace.SaaS.Accelerator.DataAccess.Entities.Subscriptions subscription,
        CancellationToken ct)
    {
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

        // Step 2: region. ALWAYS query Function1 -- it is the authority for a tenant's region; we
        // never short-circuit on a stored AzureRegion. A positively detected region is (re)applied
        // and marked complete on every load, which self-heals any stale or incomplete row. The
        // stored region is used only as a fallback to remember a MANUAL selection that Function1
        // cannot detect; if there's neither a detection nor a prior selection, we show the picker.
        if (tenantId != Guid.Empty)
        {
            var lookup = await this.regionService.GetTenantRegionAsync(tenantId, ct).ConfigureAwait(false);
            // A positively detected region is used as-is, even if it isn't in the customer-facing
            // selector list (e.g. an internal/dev region): detection wins over the offered list.
            var detected = !lookup.IsFallback
                && !string.IsNullOrEmpty(lookup.AzRegion)
                && lookup.AzRegion != "?";

            if (detected)
            {
                var storedRegion = consent?.AzureRegion;
                var matches = string.Equals(storedRegion, lookup.AzRegion, StringComparison.OrdinalIgnoreCase);

                // The stored region is never authoritative -- it's just what was picked or last
                // returned. When Function1 disagrees with it, log the discrepancy before we
                // overwrite the AMP DB with Function1's answer.
                if (storedRegion != null && !matches)
                {
                    this.logger.Warn(HttpUtility.HtmlEncode(
                        $"Function1 region '{lookup.AzRegion}' differs from stored '{storedRegion}' for subscription {subscriptionId} (tenant {tenantId}); updating AMP DB to Function1."));
                }

                // Persist + complete only when the stored row doesn't already match and isn't
                // already complete (avoids a write on every page load).
                if (storedRegion == null || !matches || !consent.TenantRegionsFanOutCompleteUtc.HasValue)
                {
                    try
                    {
                        await this.regionService.SaveRegionAsync(
                            subscriptionId, tenantId, lookup.AzRegion, this.CurrentUserEmailAddress, ct).ConfigureAwait(false);
                        consent = this.consentRepo.GetByAmpSubscriptionId(subscriptionId);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError(HttpUtility.HtmlEncode(
                            $"Auto-complete of detected region '{lookup.AzRegion}' failed for {subscriptionId}: {ex.Message}"));
                    }
                }
            }
            else if (consent?.AzureRegion == null)
            {
                // Function1 can't identify the region and none was previously selected -> manual pick.
                vm.Step2 = StepState.NotStarted;
                vm.RegionPicker = new RegionPickerViewModel
                {
                    Mode = lookup.IsFallback ? "fallback" : "picker",
                    Selectors = lookup.AzureRegionSelectors ?? new(),
                    ErrorMessage = lookup.Error,
                };
            }
            // else: not detected but a region was previously selected -> fall through to "saved".
        }
        else if (consent?.AzureRegion == null)
        {
            vm.Step2 = StepState.NotStarted;
            vm.RegionPicker = new RegionPickerViewModel
            {
                Mode = "fallback",
                ErrorMessage = "Subscription has no purchaser tenant id; cannot query Function1.",
            };
        }

        if (consent?.AzureRegion != null)
        {
            var fanOutComplete = consent.TenantRegionsFanOutCompleteUtc.HasValue;
            // The fan-out is a synchronous one-shot with NO outbox retry, so a recorded failure is
            // terminal -- surface it as Failed with the reason instead of an endless "propagating…".
            var fanOutFailed = !fanOutComplete && !string.IsNullOrEmpty(consent.FanOutFailureRegions);
            vm.Step2 = fanOutComplete
                ? StepState.Complete
                : (fanOutFailed ? StepState.Failed : StepState.InProgress);
            vm.RegionPicker = new RegionPickerViewModel
            {
                Mode = "saved",
                SelectedRegion = consent.AzureRegion,
                SelectedRegionFriendly = consent.AzureRegion,
                FanOutComplete = fanOutComplete,
                SelectedUtc = consent.AzureRegionSelectedUtc,
                SelectedByUpn = consent.AzureRegionSelectedByUpn,
                ErrorMessage = fanOutFailed ? consent.FanOutFailureRegions : null,
            };
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

        // Step 5 gating: the Teams activity app is a SECOND tenant consent (mandatory -- Teams
        // activity notifications can't be sent without it). Its only real prerequisite is that the
        // tenant is known + runtime consent has established tenant trust; it does not depend on sites.
        if (consent?.RuntimeAppConsentedUtc.HasValue != true)
        {
            vm.Step5 = StepState.Locked;
        }
        else
        {
            vm.Step5 = consent.TeamsActivityAppConsentedUtc.HasValue ? StepState.Complete : StepState.NotStarted;
        }
        vm.TeamsActivity = new ConsentStepViewModel
        {
            Granted = consent?.TeamsActivityAppConsentedUtc.HasValue ?? false,
            GrantedUtc = consent?.TeamsActivityAppConsentedUtc,
            GrantedByUpn = consent?.TeamsActivityConsentedByUpn,
        };

        return vm;
    }

    /// <summary>
    /// Renders the inline setup checklist partial, flagging the ViewData so the step partials
    /// know they're embedded in the subscriptions list (and carry a returnTo=list hint on their
    /// OAuth links). Used for the lazy-load, the polling refresh, and AJAX action responses.
    /// </summary>
    private IActionResult SetupPanel(SetupViewModel vm)
    {
        this.ViewData["Inline"] = true;
        return this.PartialView("_SetupChecklist", vm);
    }

    /// <summary>True when the request came from the inline accordion's fetch() calls.</summary>
    private bool IsAjaxRequest() =>
        string.Equals(this.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Terminal result for a mutating setup action, given the result its core produced. For a
    /// normal request the core's result (a PRG redirect to the standalone page) is returned
    /// unchanged. For an inline (AJAX) request: the normal redirect-to-<see cref="Index"/> is
    /// converted to an in-place re-render of the checklist partial (lifting any TempData flash the
    /// action set onto the model), and any OTHER redirect -- an auth bounce or an interactive-consent
    /// resume -- is handed to the client as a JSON { requiresRedirect, url } to navigate to as a full
    /// page (an XHR can't follow a 302 into the Microsoft sign-in flow).
    /// </summary>
    private async Task<IActionResult> FinishInlineAsync(Guid subscriptionId, IActionResult coreResult, CancellationToken ct)
    {
        if (!this.IsAjaxRequest())
        {
            return coreResult;
        }

        if (coreResult is RedirectToActionResult rr
            && rr.ControllerName == null
            && string.Equals(rr.ActionName, nameof(Index), StringComparison.Ordinal)
            && this.TryAuthorizeSetup(subscriptionId, out var subscription))
        {
            var vm = await this.BuildSetupViewModelAsync(subscriptionId, subscription, ct).ConfigureAwait(false);
            if (this.TempData["FlashMessage"] is string flash)
            {
                vm.FlashMessage = flash;
                vm.FlashIsError = this.TempData["FlashIsError"] is bool b && b;
            }

            return this.SetupPanel(vm);
        }

        return this.InlineRedirectJson(coreResult);
    }

    /// <summary>
    /// Wraps a redirect as a JSON { requiresRedirect, url } so the inline accordion's fetch() can
    /// perform it as a full-page navigation (used for interactive-consent resume and auth bounces).
    /// </summary>
    private IActionResult InlineRedirectJson(IActionResult redirect)
    {
        var url = redirect switch
        {
            RedirectToActionResult r => this.Url.Action(r.ActionName, r.ControllerName, r.RouteValues),
            RedirectResult rd => rd.Url,
            _ => null,
        };
        return this.Json(new { requiresRedirect = true, url = url ?? this.Url.Action(nameof(Index), new { }) });
    }

    [HttpPost("/Setup/{subscriptionId:guid}/Region")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Region(Guid subscriptionId, string azureRegion, CancellationToken ct)
    {
        var result = await this.RegionCoreAsync(subscriptionId, azureRegion, ct).ConfigureAwait(false);
        return await this.FinishInlineAsync(subscriptionId, result, ct).ConfigureAwait(false);
    }

    private async Task<IActionResult> RegionCoreAsync(Guid subscriptionId, string azureRegion, CancellationToken ct)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (!this.TryAuthorizeSetup(subscriptionId, out var subscription))
        {
            return this.SetupAccessDenied();
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
            // Manual selection: push to the fan-out endpoint now and wait. Only a confirmed
            // delivery marks Step 2 complete.
            var propagated = await this.regionService.SaveRegionAndFanOutAsync(
                subscriptionId,
                tenantId,
                azureRegion,
                this.CurrentUserEmailAddress,
                ct).ConfigureAwait(false);

            if (propagated)
            {
                this.TempData["FlashMessage"] = "Region saved and propagated to all regions.";
            }
            else
            {
                this.TempData["FlashMessage"] = "Region saved, but propagation to all regions did not complete. Please try again.";
                this.TempData["FlashIsError"] = true;
            }
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
    public IActionResult Consent(Guid subscriptionId, string returnTo = null)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (!this.TryAuthorizeSetup(subscriptionId, out var subscription))
        {
            return this.SetupAccessDenied();
        }

        // Remember whether the flow began in the subscriptions-list accordion so the callback can
        // return the user there. TempData survives the external round-trip to Microsoft (cookie-backed).
        var toList = string.Equals(returnTo, "list", StringComparison.OrdinalIgnoreCase);
        if (toList)
        {
            this.TempData["SetupConsentReturnList"] = true;
        }

        var tenantId = subscription.PurchaserTenantId ?? Guid.Empty;
        if (tenantId == Guid.Empty)
        {
            this.TempData["FlashMessage"] = "This subscription has no tenant id; contact support.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectAfterSetupFlow(subscriptionId, toList);
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
            return this.RedirectAfterSetupFlow(subscriptionId, toList);
        }

        return this.Redirect(url);
    }

    /// <summary>
    /// Synchronous redirect target for the consent/teams GET actions: back to the subscriptions
    /// list (panel re-opened) when the flow began there, otherwise the standalone setup page.
    /// </summary>
    private IActionResult RedirectAfterSetupFlow(Guid subscriptionId, bool toList)
        => toList
            ? this.RedirectToAction("Subscriptions", "Home", new { expand = subscriptionId })
            : this.RedirectToAction(nameof(Index), new { subscriptionId });

    /// <summary>
    /// Post-callback redirect target for the OAuth consent flows: honours the returnTo=list hint
    /// stashed in TempData by the originating GET (consumed here), so the user lands back where
    /// the flow began.
    /// </summary>
    private IActionResult RedirectAfterConsentCallback(Guid subscriptionId)
        => this.TempData["SetupConsentReturnList"] is bool b && b
            ? this.RedirectToAction("Subscriptions", "Home", new { expand = subscriptionId })
            : this.RedirectToAction(nameof(Index), new { subscriptionId });

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
            return this.RedirectAfterConsentCallback(subscriptionId.Value);
        }

        var granted = string.Equals(admin_consent, "True", StringComparison.OrdinalIgnoreCase);
        if (!granted)
        {
            this.TempData["FlashMessage"] = "Consent was not granted.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectAfterConsentCallback(subscriptionId.Value);
        }

        await this.consentService.RecordConsentAsync(
            subscriptionId.Value,
            this.CurrentUserEmailAddress,
            objectId: null,
            ct).ConfigureAwait(false);

        this.TempData["FlashMessage"] = "Tenant consent recorded.";
        return this.RedirectAfterConsentCallback(subscriptionId.Value);
    }

    [HttpGet("/Setup/{subscriptionId:guid}/TeamsActivity")]
    public IActionResult TeamsActivity(Guid subscriptionId, string returnTo = null)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (!this.TryAuthorizeSetup(subscriptionId, out var subscription))
        {
            return this.SetupAccessDenied();
        }

        var toList = string.Equals(returnTo, "list", StringComparison.OrdinalIgnoreCase);
        if (toList)
        {
            this.TempData["SetupConsentReturnList"] = true;
        }

        var tenantId = subscription.PurchaserTenantId ?? Guid.Empty;
        if (tenantId == Guid.Empty)
        {
            this.TempData["FlashMessage"] = "This subscription has no tenant id; contact support.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectAfterSetupFlow(subscriptionId, toList);
        }

        var callbackUri = $"{this.Request.Scheme}://{this.Request.Host}/api/setup/teams-consent-callback";
        string url;
        try
        {
            url = this.consentService.BuildTeamsActivityConsentUrl(tenantId, subscriptionId, callbackUri);
        }
        catch (InvalidOperationException ex)
        {
            this.logger.LogError($"BuildTeamsActivityConsentUrl failed: {ex.Message}");
            this.TempData["FlashMessage"] = "The Teams activity app is not configured on this Accelerator. Contact support.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectAfterSetupFlow(subscriptionId, toList);
        }

        return this.Redirect(url);
    }

    [HttpGet("/api/setup/teams-consent-callback")]
    public async Task<IActionResult> TeamsActivityConsentCallback(
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
            this.logger.LogError("Teams consent callback received invalid or expired state");
            return this.RedirectToAction("Index", "Home");
        }

        var sub = this.subscriptionRepo.GetById(subscriptionId.Value);
        if (sub == null)
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (!string.IsNullOrEmpty(error))
        {
            this.logger.LogError($"Teams consent callback error for {subscriptionId}: {error} - {error_description}");
            this.TempData["FlashMessage"] = $"Teams activity consent was not granted: {error}";
            this.TempData["FlashIsError"] = true;
            return this.RedirectAfterConsentCallback(subscriptionId.Value);
        }

        var granted = string.Equals(admin_consent, "True", StringComparison.OrdinalIgnoreCase);
        if (!granted)
        {
            this.TempData["FlashMessage"] = "Teams activity consent was not granted.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectAfterConsentCallback(subscriptionId.Value);
        }

        await this.consentService.RecordTeamsActivityConsentAsync(
            subscriptionId.Value,
            this.CurrentUserEmailAddress,
            objectId: null,
            ct).ConfigureAwait(false);

        this.TempData["FlashMessage"] = "Teams activity app consent recorded.";
        return this.RedirectAfterConsentCallback(subscriptionId.Value);
    }

    [HttpGet("/Setup/{subscriptionId:guid}/Status.json")]
    public IActionResult Status(Guid subscriptionId)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.Unauthorized();
        }

        if (!this.TryAuthorizeSetup(subscriptionId, out _))
        {
            return this.NotFound();
        }

        var consent = this.consentRepo.GetByAmpSubscriptionId(subscriptionId);
        var sites = this.siteRepo.ListBySubscription(subscriptionId);
        return this.Json(new
        {
            regionSelected = consent?.AzureRegion != null,
            regionFanOutComplete = consent?.TenantRegionsFanOutCompleteUtc.HasValue ?? false,
            // Non-null only when the one-shot fan-out failed and hasn't since completed -- lets the poller
            // stop spinning "propagating…" and show the reason.
            regionFanOutFailure = (consent?.TenantRegionsFanOutCompleteUtc.HasValue ?? false)
                ? null
                : consent?.FanOutFailureRegions,
            consented = consent?.RuntimeAppConsentedUtc.HasValue ?? false,
            teamsActivityConsented = consent?.TeamsActivityAppConsentedUtc.HasValue ?? false,
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
            var result = await this.AddSiteCoreAsync(subscriptionId, sharePointSiteUrl, ct).ConfigureAwait(false);
            return await this.FinishInlineAsync(subscriptionId, result, ct).ConfigureAwait(false);
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            var resume = this.RedirectToAction(nameof(ResumeAddSite), new { subscriptionId, sharePointSiteUrl });
            return this.IsAjaxRequest() ? this.InlineRedirectJson(resume) : resume;
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

        if (!this.TryAuthorizeSetup(subscriptionId, out _))
        {
            return this.SetupAccessDenied();
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

    [HttpPost("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/Delete")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RemoveSite(Guid subscriptionId, int siteId, CancellationToken ct)
        => this.RunRemoveSiteOrChallengeAsync(subscriptionId, siteId, ct);

    /// <summary>
    /// POST wrapper for RemoveSite. Revoking the runtime app's per-site grant needs a delegated
    /// Graph token; a silent-acquisition failure throws
    /// <see cref="MicrosoftIdentityWebChallengeUserException"/>, which we hand off to the GET
    /// <see cref="ResumeRemoveSite"/> action for interactive consent, mirroring the grant flow.
    /// </summary>
    private async Task<IActionResult> RunRemoveSiteOrChallengeAsync(Guid subscriptionId, int siteId, CancellationToken ct)
    {
        try
        {
            var result = await this.RemoveSiteCoreAsync(subscriptionId, siteId, ct).ConfigureAwait(false);
            return await this.FinishInlineAsync(subscriptionId, result, ct).ConfigureAwait(false);
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            var resume = this.RedirectToAction(nameof(ResumeRemoveSite), new { subscriptionId, siteId });
            return this.IsAjaxRequest() ? this.InlineRedirectJson(resume) : resume;
        }
    }

    /// <summary>
    /// Post-interactive-consent resume target for RemoveSite. Reached only via redirect from
    /// <see cref="RunRemoveSiteOrChallengeAsync"/>; [AuthorizeForScopes] drives the sign-in and
    /// returns the user here, by which point the delegated token is cached and the revoke +
    /// delete can complete. Access is still gated by the tenant ownership check inside the core.
    /// </summary>
    [HttpGet("/Setup/{subscriptionId:guid}/Sites/{siteId:int}/Delete/Resume")]
    [AuthorizeForScopes(Scopes = new[] { "https://graph.microsoft.com/Sites.FullControl.All" })]
    public Task<IActionResult> ResumeRemoveSite(Guid subscriptionId, int siteId, CancellationToken ct)
        => this.RemoveSiteCoreAsync(subscriptionId, siteId, ct);

    /// <summary>
    /// Revokes the runtime app's per-site grant in Graph (when one exists) and then hard-removes
    /// the enrollment row. If the revoke fails the row is left intact so the customer can retry --
    /// we never drop the row while the app could still hold access to the site.
    /// </summary>
    private async Task<IActionResult> RemoveSiteCoreAsync(Guid subscriptionId, int siteId, CancellationToken ct)
    {
        if (!this.User.Identity.IsAuthenticated)
        {
            return this.RedirectToAction("Index", "Home");
        }

        if (!this.TryAuthorizeSetup(subscriptionId, out _))
        {
            return this.SetupAccessDenied();
        }

        var site = this.siteRepo.Get(siteId);
        if (site == null || site.AmpSubscriptionId != subscriptionId)
        {
            this.TempData["FlashMessage"] = "Site not found.";
            this.TempData["FlashIsError"] = true;
            return this.RedirectToAction(nameof(Index), new { subscriptionId });
        }

        var siteUrl = site.SharePointSiteUrl;

        // Only touch Graph when the runtime app actually holds a grant. A Pending/Failed
        // enrollment that was never granted has nothing to revoke, so we skip the token
        // acquisition entirely (and therefore never force an interactive prompt just to
        // delete a never-granted row).
        if (!string.IsNullOrWhiteSpace(site.PermissionId) && !string.IsNullOrWhiteSpace(site.GraphSiteId))
        {
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

            try
            {
                await this.sitePermissionService.RevokeAsync(site, token, ct).ConfigureAwait(false);
            }
            catch (MicrosoftIdentityWebChallengeUserException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogError(HttpUtility.HtmlEncode(
                    $"Setup site revoke failed: subscription={subscriptionId} site={siteId} {siteUrl}: {ex.Message}"));
                this.TempData["FlashMessage"] = $"Couldn't remove the app's permission on '{siteUrl}'. Nothing was deleted; please try again.";
                this.TempData["FlashIsError"] = true;
                return this.RedirectToAction(nameof(Index), new { subscriptionId });
            }
        }

        this.siteRepo.Remove(siteId);

        this.logger.Info(HttpUtility.HtmlEncode(
            $"Setup site removed: {subscriptionId} {siteUrl} by {this.CurrentUserEmailAddress}"));
        this.TempData["FlashMessage"] = $"Site '{siteUrl}' removed.";
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
            var result = await this.TransitionSiteRoleAsync(subscriptionId, siteId, transition, ct).ConfigureAwait(false);
            return await this.FinishInlineAsync(subscriptionId, result, ct).ConfigureAwait(false);
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            var resume = this.RedirectToAction(nameof(ResumeSiteTransition), new { subscriptionId, siteId, transition = transition.ToString() });
            return this.IsAjaxRequest() ? this.InlineRedirectJson(resume) : resume;
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

        if (!this.TryAuthorizeSetup(subscriptionId, out _))
        {
            return this.SetupAccessDenied();
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
