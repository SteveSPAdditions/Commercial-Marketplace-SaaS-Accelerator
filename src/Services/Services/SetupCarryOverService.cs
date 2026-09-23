// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Resubscribe carry-over: copy STRUCTURE, not STATE.
///
/// A resubscribe issues a BRAND NEW Marketplace subscription id, but Setup's tables key on that id,
/// so a fully-provisioned tenant returns to an empty checklist. Of the four persisted steps, the
/// region and the site list need carrying:
///
///   Step 2 (region)      -- carried AT ACTIVATION (CarryOverRegionFromPreviousSubscription, called
///                           from PendingActivationStatusHandler). SetupController's per-load
///                           Function1 re-query self-heals this too, but only once the customer
///                           opens Setup -- and the reconcile snapshot (ReconcileController) emits
///                           only consent rows WITH a region, so until that visit the snapshot kept
///                           naming the dead predecessor and RAU's daily reconcile could revert its
///                           TenantRegions row to it (observed 2026-09-23 on a test tenant whose
///                           free-plan resubscribe auto-activated with no Setup visit).
///   Steps 3 + 5 (consent)-- deliberately NOT carried. These are tenant-wide Entra grants whose
///                           truth lives in the customer's tenant, not here, and the 7-day
///                           post-unsubscribe purge can revoke them. Re-consent is one idempotent
///                           click; a wrongly-inherited "Complete" is a silently broken tenant.
///   Step 4 (sites)       -- carried AT ACTIVATION too (CarryOverFromPreviousSubscription, same
///                           caller), so it runs exactly once per subscription by construction.
///                           Seeding on Setup load re-seeded the sites every time the customer
///                           emptied the list, because the predecessor's rows never go away.
///                           Carried as un-granted rows. The URL + resolved Graph site id are
///                           stable facts worth keeping (they save the customer re-typing and
///                           re-validating every site); Granted/GrantedUtc/PermissionId/CurrentRole
///                           are live permission state that the purge may have revoked, so they are
///                           dropped. The customer presses Grant, which re-establishes the
///                           permission for real and is harmless if it survived.
/// </summary>
public class SetupCarryOverService : ISetupCarryOverService
{
    private readonly ISubscriptionTenantConsentRepository consentRepo;
    private readonly ISubscriptionSiteRepository siteRepo;
    private readonly ISubscriptionsRepository subscriptionsRepo;
    private readonly ILogger<SetupCarryOverService> logger;

    public SetupCarryOverService(
        ISubscriptionTenantConsentRepository consentRepo,
        ISubscriptionSiteRepository siteRepo,
        ISubscriptionsRepository subscriptionsRepo,
        ILogger<SetupCarryOverService> logger)
    {
        this.consentRepo = consentRepo;
        this.siteRepo = siteRepo;
        this.subscriptionsRepo = subscriptionsRepo;
        this.logger = logger;
    }

    public int CarryOverFromPreviousSubscription(Guid newAmpSubscriptionId, Guid tenantId)
    {
        if (tenantId == Guid.Empty || newAmpSubscriptionId == Guid.Empty)
        {
            return 0;
        }

        try
        {
            // Safety check, keyed on the SITE table itself: once the new subscription has any site
            // row there is progress to protect and nothing left to seed. Not the once-only guard
            // (activation being a one-time transition is), just a belt-and-braces against a
            // re-run. Deliberately NOT keyed on "no consent row": the region carry-over mints that
            // row moments before this runs.
            var existingSites = this.siteRepo.ListBySubscription(newAmpSubscriptionId).ToList();
            if (existingSites.Count > 0)
            {
                return 0;
            }

            var prior = FindEndedPredecessor(newAmpSubscriptionId, tenantId);
            if (prior == null)
            {
                return 0;
            }

            var priorSites = this.siteRepo.ListBySubscription(prior.AmpSubscriptionId).ToList();
            if (priorSites.Count == 0)
            {
                return 0;
            }

            // Defensive: never seed the same URL twice, even if the prior list somehow repeats one.
            var existingUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            var seeded = 0;
            foreach (var priorSite in priorSites)
            {
                if (string.IsNullOrWhiteSpace(priorSite.SharePointSiteUrl)
                    || !existingUrls.Add(priorSite.SharePointSiteUrl))
                {
                    continue;
                }

                this.siteRepo.Save(new SubscriptionSite
                {
                    AmpSubscriptionId = newAmpSubscriptionId,
                    SharePointSiteUrl = priorSite.SharePointSiteUrl,

                    // The Graph site id is a property of the site itself, not of the grant, so it
                    // stays valid across subscriptions and spares the customer a re-validation.
                    GraphSiteId = priorSite.GraphSiteId,

                    // Structure only. No CurrentRole, PermissionId, GrantedUtc or GrantedByUpn:
                    // whether the runtime app still holds a permission on this site is not something
                    // this row is entitled to assert.
                    Status = "Pending",
                    CreatedUtc = now,
                });
                seeded++;
            }

            if (seeded > 0)
            {
                this.logger.LogInformation(
                    "Setup carry-over: seeded {Count} site(s) onto resubscribed {NewSubscriptionId} from {PriorSubscriptionId} (tenant {TenantId}). Sites are Pending -- grants are NOT carried over.",
                    seeded, newAmpSubscriptionId, prior.AmpSubscriptionId, tenantId);
            }

            return seeded;
        }
        catch (Exception ex)
        {
            // Best-effort convenience. A failure here must never block Setup -- the customer can
            // still enrol their sites by hand.
            this.logger.LogError(
                ex,
                "Setup carry-over failed for {NewSubscriptionId} (tenant {TenantId}); continuing with an empty site list.",
                newAmpSubscriptionId, tenantId);
            return 0;
        }
    }

    public bool CarryOverRegionFromPreviousSubscription(Guid newAmpSubscriptionId, Guid tenantId)
    {
        if (tenantId == Guid.Empty || newAmpSubscriptionId == Guid.Empty)
        {
            return false;
        }

        try
        {
            // A row already exists -> Setup (or an earlier activation) has spoken for this
            // subscription; whatever region it holds, or lacks, is not ours to overwrite.
            if (this.consentRepo.GetByAmpSubscriptionId(newAmpSubscriptionId) != null)
            {
                return false;
            }

            var prior = FindEndedPredecessor(newAmpSubscriptionId, tenantId);
            if (prior == null || string.IsNullOrWhiteSpace(prior.AzureRegion))
            {
                return false;
            }

            // Region only. Mirrors AzureRegionService.SaveRegionAsync's "detected region" shape:
            // resolving IS the fan-out completion -- RAU already holds the tenant's TenantRegions row
            // (the Activated signal adopts the new subscription id onto it), and the daily reconcile
            // ratifies the rest. The metered-user threshold and every consent timestamp are left
            // unset: the threshold is captured per subscription at manual activation, and consent
            // is a tenant-wide Entra grant whose truth is re-established in Setup.
            var now = DateTime.UtcNow;
            this.consentRepo.Save(new SubscriptionTenantConsent
            {
                AmpSubscriptionId = newAmpSubscriptionId,
                TenantId = tenantId,
                AzureRegion = prior.AzureRegion,
                AzureRegionSelectedUtc = now,
                AzureRegionSelectedByUpn = RegionCarryOverActor,
                TenantRegionsFanOutCompleteUtc = now,
                FanOutFailureRegions = null,
            });

            this.logger.LogInformation(
                "Setup carry-over: region {Region} carried onto resubscribed {NewSubscriptionId} from {PriorSubscriptionId} (tenant {TenantId}) at activation. Consent and grants are NOT carried over.",
                prior.AzureRegion, newAmpSubscriptionId, prior.AmpSubscriptionId, tenantId);
            return true;
        }
        catch (Exception ex)
        {
            // Best-effort. Activation has already succeeded at Microsoft; Setup's own per-load
            // region self-heal still covers this subscription the next time it is opened.
            this.logger.LogError(
                ex,
                "Region carry-over failed for {NewSubscriptionId} (tenant {TenantId}); Setup will self-heal the region on its next load.",
                newAmpSubscriptionId, tenantId);
            return false;
        }
    }

    /// <summary>Recorded as the region's selector so the Setup panel shows where the value came from.</summary>
    public const string RegionCarryOverActor = "carry-over (resubscribe)";

    /// <summary>
    /// The tenant's most recent consent row for a subscription OTHER than the new one, provided
    /// that subscription has actually ENDED. For a tenant holding two concurrent subscriptions the
    /// most recent other row would be the LIVE one -- carrying anything from that would silently
    /// merge two subscriptions' state. Null when there is no such predecessor.
    /// </summary>
    private SubscriptionTenantConsent FindEndedPredecessor(Guid newAmpSubscriptionId, Guid tenantId)
    {
        var prior = this.consentRepo.GetPreviousByTenantId(tenantId, newAmpSubscriptionId);
        if (prior == null || prior.AmpSubscriptionId == newAmpSubscriptionId)
        {
            return null;
        }

        var priorSubscription = this.subscriptionsRepo.GetById(prior.AmpSubscriptionId, true);
        if (priorSubscription == null
            || !string.Equals(
                priorSubscription.SubscriptionStatus,
                SubscriptionStatusEnumExtension.Unsubscribed.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return prior;
    }
}
