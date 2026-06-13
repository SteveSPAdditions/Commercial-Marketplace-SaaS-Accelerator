// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.Services.Models;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Client for the Read and Understood AzRSvc Function1 service and the
/// downstream fan-out of the TenantRegions row.
/// </summary>
public interface IAzureRegionService
{
    /// <summary>
    /// Look up the tenant's region and the publisher-maintained selector list.
    /// On Function1 failure, returns a fallback shape with IsFallback=true.
    /// </summary>
    Task<TenantRegionInfo> GetTenantRegionAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Persist a DETECTED region and mark the tenant-region fan-out complete immediately.
    /// Propagation of the TenantRegions row into the regional databases is left to the daily
    /// ratification (reconcile) job, so this does not call the signaling endpoint.
    /// </summary>
    Task SaveRegionAsync(
        Guid ampSubscriptionId,
        Guid tenantId,
        string azureRegion,
        string actorUpn,
        CancellationToken ct);

    /// <summary>
    /// Persist a MANUALLY SELECTED region and synchronously push it to the fan-out (signaling)
    /// endpoint, waiting for the result. Marks the fan-out complete only on confirmed delivery.
    /// Returns true if propagated (complete), false if the push did not succeed (region is saved
    /// but not complete — the caller should surface a retry).
    /// </summary>
    Task<bool> SaveRegionAndFanOutAsync(
        Guid ampSubscriptionId,
        Guid tenantId,
        string azureRegion,
        string actorUpn,
        CancellationToken ct);
}
