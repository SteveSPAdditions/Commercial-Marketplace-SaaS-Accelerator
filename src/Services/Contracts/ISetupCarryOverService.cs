// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Carries a tenant's enrolled SharePoint site list forward when it resubscribes.
/// </summary>
public interface ISetupCarryOverService
{
    /// <summary>
    /// Seeds a brand-new subscription's Setup state from the same tenant's previous, now
    /// unsubscribed one. Copies the site list as un-granted rows; never copies grant or consent
    /// state. No-op unless this is the subscription's first Setup load.
    /// </summary>
    /// <param name="newAmpSubscriptionId">The subscription being set up.</param>
    /// <param name="tenantId">The purchaser tenant, used to find the previous subscription.</param>
    /// <returns>The number of site rows seeded (0 when nothing was carried over).</returns>
    int CarryOverFromPreviousSubscription(Guid newAmpSubscriptionId, Guid tenantId);
}
