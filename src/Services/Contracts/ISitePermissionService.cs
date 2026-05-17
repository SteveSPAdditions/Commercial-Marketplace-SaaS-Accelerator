// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Issues, queries, and downgrades per-site Sites.Selected grants against the
/// Read and Understood runtime Entra app via Microsoft Graph.
/// </summary>
public interface ISitePermissionService
{
    /// <summary>
    /// Resolve a SharePoint site URL to its Graph site id
    /// ({hostname},{spsiteid},{spwebid}).
    /// </summary>
    Task<string> ResolveGraphSiteIdAsync(
        string sharePointSiteUrl,
        string delegatedAccessToken,
        CancellationToken ct);

    /// <summary>
    /// Grant the runtime app per-site access at role "manage".
    /// Idempotent: returns the existing permission id if one already exists.
    /// </summary>
    Task<GrantResult> GrantManageAsync(
        string graphSiteId,
        string delegatedAccessToken,
        CancellationToken ct);

    /// <summary>Downgrade an existing per-site grant from manage to read.</summary>
    Task DowngradeToReadAsync(
        SubscriptionSite site,
        string delegatedAccessToken,
        CancellationToken ct);

    /// <summary>Re-elevate an existing per-site grant from read back to manage.</summary>
    Task ReelevateToManageAsync(
        SubscriptionSite site,
        string delegatedAccessToken,
        CancellationToken ct);
}

/// <summary>Outcome of a grant call.</summary>
public class GrantResult
{
    public string PermissionId { get; set; }
    public bool AlreadyExisted { get; set; }
}
