// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Microsoft Graph implementation of <see cref="ISitePermissionService"/>. Issues,
/// queries, and adjusts per-site Sites.Selected grants for the Read and Understood
/// runtime app against the customer's tenant.
///
/// Graph endpoints used:
///   GET   /v1.0/sites/{hostname}:{path}                 resolve URL -> siteId
///   GET   /v1.0/sites/{siteId}/permissions              find existing grant
///   POST  /v1.0/sites/{siteId}/permissions              create grant
///   PATCH /v1.0/sites/{siteId}/permissions/{permId}     change role
///
/// The runtime app needs Sites.FullControl.All (Application) consented in the
/// customer's tenant so it can grant itself per-site Sites.Selected access.
/// Step 3 of Setup is where that admin consent happens.
/// </summary>
public class SitePermissionService : ISitePermissionService
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    private readonly HttpClient httpClient;
    private readonly SaaSApiClientConfiguration config;
    private readonly ILogger<SitePermissionService> logger;

    public SitePermissionService(
        HttpClient httpClient,
        SaaSApiClientConfiguration config,
        ILogger<SitePermissionService> logger)
    {
        this.httpClient = httpClient;
        this.config = config;
        this.logger = logger;
    }

    public async Task<string> ResolveGraphSiteIdAsync(
        string sharePointSiteUrl,
        string accessToken,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(sharePointSiteUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid SharePoint site URL: {sharePointSiteUrl}", nameof(sharePointSiteUrl));
        }

        var hostname = uri.Host;
        var serverRelativePath = uri.AbsolutePath.TrimEnd('/');
        // Graph: GET /sites/{hostname}:/sites/policies (note the colon separator)
        var url = $"{GraphBaseUrl}/sites/{hostname}:{serverRelativePath}?$select=id";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

        using var resp = await this.httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Graph site lookup failed for '{sharePointSiteUrl}': HTTP {(int)resp.StatusCode} -- {Snip(body)}");
        }

        var id = (string)JsonNode.Parse(body)?["id"];
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException($"Graph returned no site id for '{sharePointSiteUrl}': {Snip(body)}");
        }
        return id;
    }

    public async Task<GrantResult> GrantManageAsync(
        string graphSiteId,
        string accessToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(this.config.RuntimeAppClientId))
        {
            throw new InvalidOperationException("RuntimeAppClientId is not configured");
        }

        // Idempotency: if a permission for this site already exists, reuse it. Avoids
        // duplicate permission objects when the caller retries on transient failures.
        // Role is "manage" -- "write" is insufficient for R&U enable-library which needs
        // ManageLists (create the shadow list + add Document Selector columns). Customer
        // can downgrade to "read" via the portal once enable-library has completed.
        var existing = await FindExistingPermissionAsync(graphSiteId, accessToken, ct).ConfigureAwait(false);
        if (existing != null)
        {
            if (!string.Equals(existing.Role, "manage", StringComparison.OrdinalIgnoreCase))
            {
                await PatchRoleAsync(graphSiteId, existing.PermissionId, "manage", accessToken, ct).ConfigureAwait(false);
            }
            return new GrantResult { PermissionId = existing.PermissionId, AlreadyExisted = true };
        }

        var url = $"{GraphBaseUrl}/sites/{graphSiteId}/permissions";
        var payload = new
        {
            roles = new[] { "manage" },
            grantedToIdentities = new[]
            {
                new
                {
                    application = new
                    {
                        id = this.config.RuntimeAppClientId,
                        displayName = "Read and Understood Runtime",
                    },
                },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

        using var resp = await this.httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Graph permission create failed for site {graphSiteId}: HTTP {(int)resp.StatusCode} -- {Snip(body)}");
        }

        var permissionId = (string)JsonNode.Parse(body)?["id"];
        if (string.IsNullOrWhiteSpace(permissionId))
        {
            throw new InvalidOperationException($"Graph returned no permission id: {Snip(body)}");
        }
        return new GrantResult { PermissionId = permissionId, AlreadyExisted = false };
    }

    public Task DowngradeToReadAsync(SubscriptionSite site, string accessToken, CancellationToken ct)
        => PatchRoleAsync(site.GraphSiteId, site.PermissionId, "read", accessToken, ct);

    public Task ReelevateToManageAsync(SubscriptionSite site, string accessToken, CancellationToken ct)
        => PatchRoleAsync(site.GraphSiteId, site.PermissionId, "manage", accessToken, ct);

    public async Task RevokeAsync(SubscriptionSite site, string accessToken, CancellationToken ct)
    {
        // Nothing was ever granted (e.g. a Pending/Failed enrollment) -> nothing to revoke.
        if (string.IsNullOrWhiteSpace(site.GraphSiteId) || string.IsNullOrWhiteSpace(site.PermissionId))
        {
            return;
        }

        var url = $"{GraphBaseUrl}/sites/{site.GraphSiteId}/permissions/{site.PermissionId}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

        using var resp = await this.httpClient.SendAsync(req, ct).ConfigureAwait(false);

        // Already gone -> treat as success (idempotent delete).
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Graph permission DELETE failed for site {site.GraphSiteId} permission {site.PermissionId}: HTTP {(int)resp.StatusCode} -- {Snip(body)}");
        }
    }

    private async Task PatchRoleAsync(string graphSiteId, string permissionId, string role, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(graphSiteId) || string.IsNullOrWhiteSpace(permissionId))
        {
            throw new InvalidOperationException("Site has no Graph site id or permission id stored; cannot change role.");
        }

        var url = $"{GraphBaseUrl}/sites/{graphSiteId}/permissions/{permissionId}";
        var payload = new { roles = new[] { role } };

        using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(payload) };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

        using var resp = await this.httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Graph permission PATCH failed for site {graphSiteId} permission {permissionId}: HTTP {(int)resp.StatusCode} -- {Snip(body)}");
        }
    }

    private async Task<ExistingPermission> FindExistingPermissionAsync(string graphSiteId, string accessToken, CancellationToken ct)
    {
        var url = $"{GraphBaseUrl}/sites/{graphSiteId}/permissions";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

        using var resp = await this.httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Graph permissions list failed for site {graphSiteId}: HTTP {(int)resp.StatusCode} -- {Snip(body)}");
        }

        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var arr = JsonNode.Parse(raw)?["value"]?.AsArray();
        if (arr == null) return null;

        foreach (var item in arr)
        {
            // Match by appId in grantedToIdentities -- the only permissions we care about are
            // the ones we created for the runtime app. Site collection administrators and
            // similar built-in grants also appear in this list.
            var identities = item?["grantedToIdentities"]?.AsArray()
                          ?? item?["grantedToIdentitiesV2"]?.AsArray();
            if (identities == null) continue;

            foreach (var ident in identities)
            {
                var appId = (string)ident?["application"]?["id"];
                if (string.Equals(appId, this.config.RuntimeAppClientId, StringComparison.OrdinalIgnoreCase))
                {
                    var permissionId = (string)item?["id"];
                    var roles = item?["roles"]?.AsArray();
                    var firstRole = roles != null && roles.Count > 0 ? (string)roles[0] : null;
                    return new ExistingPermission { PermissionId = permissionId, Role = firstRole };
                }
            }
        }
        return null;
    }

    private static string Snip(string s) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 512 ? s : s.Substring(0, 512));

    private class ExistingPermission
    {
        public string PermissionId { get; set; }
        public string Role { get; set; }
    }
}
