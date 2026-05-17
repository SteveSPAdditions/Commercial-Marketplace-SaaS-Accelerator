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
    /// Persist the region selection on SubscriptionTenantConsent and enqueue
    /// a NotificationOutbox row for the downstream fan-out. Atomic.
    /// </summary>
    Task SaveRegionAndEnqueueFanOutAsync(
        Guid ampSubscriptionId,
        Guid tenantId,
        string azureRegion,
        string actorUpn,
        CancellationToken ct);
}
