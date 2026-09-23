// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Carries a tenant's region and enrolled SharePoint site list forward when it resubscribes.
/// </summary>
public interface ISetupCarryOverService
{
    /// <summary>
    /// Seeds a brand-new subscription's Setup state from the same tenant's previous, now
    /// unsubscribed one. Copies the site list as un-granted rows; never copies grant or consent
    /// state. Called once, at activation (PendingActivationStatusHandler), never on Setup load.
    /// No-op once the new subscription has any site rows of its own.
    /// </summary>
    /// <param name="newAmpSubscriptionId">The subscription being set up.</param>
    /// <param name="tenantId">The purchaser tenant, used to find the previous subscription.</param>
    /// <returns>The number of site rows seeded (0 when nothing was carried over).</returns>
    int CarryOverFromPreviousSubscription(Guid newAmpSubscriptionId, Guid tenantId);

    /// <summary>
    /// Mints the new subscription's consent row with the region of the same tenant's previous, now
    /// unsubscribed subscription, so the reconcile snapshot names the new subscription immediately
    /// rather than only after the customer next opens Setup. Region only; consent and grant state
    /// are never carried. No-op when the new subscription already has a consent row.
    /// </summary>
    /// <param name="newAmpSubscriptionId">The subscription that has just activated.</param>
    /// <param name="tenantId">The purchaser tenant, used to find the previous subscription.</param>
    /// <returns>True when a row was minted.</returns>
    bool CarryOverRegionFromPreviousSubscription(Guid newAmpSubscriptionId, Guid tenantId);
}
