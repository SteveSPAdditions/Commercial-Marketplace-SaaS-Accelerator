// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
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
/// so a fully-provisioned tenant returns to an empty checklist. Of the four persisted steps, only
/// the site list actually needs carrying:
///
///   Step 2 (region)      -- self-heals already: SetupController re-queries Function1 by tenant on
///                           every panel load and re-saves under the new subscription id.
///   Steps 3 + 5 (consent)-- deliberately NOT carried. These are tenant-wide Entra grants whose
///                           truth lives in the customer's tenant, not here, and the 7-day
///                           post-unsubscribe purge can revoke them. Re-consent is one idempotent
///                           click; a wrongly-inherited "Complete" is a silently broken tenant.
///   Step 4 (sites)       -- carried as un-granted rows. The URL + resolved Graph site id are
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
            // Idempotency + safety in one check. A consent row is created the first time Setup runs
            // for a subscription (the region self-heal writes it), so "no row" means this is the
            // very first Setup load and there is no progress to clobber. It also guarantees the
            // tenant lookup below cannot return this subscription's own row.
            if (this.consentRepo.GetByAmpSubscriptionId(newAmpSubscriptionId) != null)
            {
                return 0;
            }

            var prior = this.consentRepo.GetByTenantId(tenantId);
            if (prior == null || prior.AmpSubscriptionId == newAmpSubscriptionId)
            {
                return 0;
            }

            // Only carry forward from a subscription that has actually ENDED. GetByTenantId returns
            // the most recent row for the tenant, which for a tenant holding two concurrent
            // subscriptions would be the other LIVE one -- seeding from that would silently merge
            // two subscriptions' site lists.
            var priorSubscription = this.subscriptionsRepo.GetById(prior.AmpSubscriptionId, true);
            if (priorSubscription == null
                || !string.Equals(
                    priorSubscription.SubscriptionStatus,
                    SubscriptionStatusEnumExtension.Unsubscribed.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var priorSites = this.siteRepo.ListBySubscription(prior.AmpSubscriptionId).ToList();
            if (priorSites.Count == 0)
            {
                return 0;
            }

            // Defensive: never double-seed a URL that somehow already exists on the new subscription.
            var existingUrls = this.siteRepo.ListBySubscription(newAmpSubscriptionId)
                .Select(s => s.SharePointSiteUrl)
                .Where(u => u != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
}
