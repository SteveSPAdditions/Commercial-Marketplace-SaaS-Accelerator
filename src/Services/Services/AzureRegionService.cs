// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;
using Microsoft.EntityFrameworkCore.Storage;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Calls AzRSvc Function1 for tenant-region lookup + selectors, and atomically
/// persists region selection + enqueues the cross-region fan-out event.
/// </summary>
public class AzureRegionService : IAzureRegionService
{
    private readonly HttpClient httpClient;
    private readonly SaaSApiClientConfiguration config;
    private readonly ISubscriptionTenantConsentRepository consentRepo;
    private readonly ISubscriptionsRepository subscriptionsRepo;
    private readonly IOutboxDispatcher dispatcher;

    public AzureRegionService(
        HttpClient httpClient,
        SaaSApiClientConfiguration config,
        ISubscriptionTenantConsentRepository consentRepo,
        ISubscriptionsRepository subscriptionsRepo,
        IOutboxDispatcher dispatcher)
    {
        this.httpClient = httpClient;
        this.config = config;
        this.consentRepo = consentRepo;
        this.subscriptionsRepo = subscriptionsRepo;
        this.dispatcher = dispatcher;
    }

    public async Task<TenantRegionInfo> GetTenantRegionAsync(Guid tenantId, CancellationToken ct)
    {
        var candidates = this.BuildCandidateUrls();
        if (candidates.Count == 0)
        {
            return this.FallbackResponse("No AzRegionSvc endpoints configured (set AzRegionSvcUrlTemplate + AzRegionSvcRegions, or AzRegionSvcUrl)");
        }

        var errors = new List<string>();
        foreach (var (region, url) in candidates)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(new { tenantId = tenantId.ToString() }),
                };
                using var resp = await this.httpClient.SendAsync(req, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"[{region}] HTTP {(int)resp.StatusCode}");
                    continue;
                }

                var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var parsed = await JsonSerializer.DeserializeAsync<Function1Response>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    ct).ConfigureAwait(false);

                if (parsed == null)
                {
                    errors.Add($"[{region}] empty body");
                    continue;
                }

                var azRegion = string.IsNullOrWhiteSpace(parsed.AzRegion) ? "?" : parsed.AzRegion;
                if (azRegion == "?")
                {
                    // This region's Function1 reachable but couldn't identify the tenant; try next.
                    errors.Add($"[{region}] AzRegion=?");
                    continue;
                }

                return new TenantRegionInfo
                {
                    AzRegion = azRegion,
                    AzureRegionSelectors = parsed.AzureRegionSelectors ?? new List<RegionSelector>(),
                    Error = parsed.Error,
                    IsFallback = false,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"[{region}] {ex.GetType().Name}: {ex.Message}");
            }
        }

        return this.FallbackResponse("All AzRegionSvc endpoints failed: " + string.Join("; ", errors));
    }

    /// <summary>
    /// Builds the ordered list of AzRSvc URLs to try. If the multi-region template is configured,
    /// returns one entry per region in randomised order. Otherwise falls back to the legacy single
    /// <see cref="SaaSApiClientConfiguration.AzRegionSvcUrl"/>.
    /// </summary>
    private List<(string Region, string Url)> BuildCandidateUrls()
    {
        var template = this.config.AzRegionSvcUrlTemplate;
        var regionsCsv = this.config.AzRegionSvcRegions;

        if (!string.IsNullOrWhiteSpace(template)
            && !string.IsNullOrWhiteSpace(regionsCsv)
            && template.IndexOf("{region}", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var list = new List<(string, string)>();
            foreach (var raw in regionsCsv.Split(','))
            {
                var slug = raw.Trim();
                if (slug.Length == 0)
                {
                    continue;
                }
                var url = template.Replace("{region}", slug, StringComparison.OrdinalIgnoreCase);
                list.Add((slug, url));
            }
            Shuffle(list);
            return list;
        }

        if (!string.IsNullOrWhiteSpace(this.config.AzRegionSvcUrl))
        {
            return new List<(string, string)> { ("legacy", this.config.AzRegionSvcUrl) };
        }

        return new List<(string, string)>();
    }

    private static void Shuffle<T>(IList<T> list)
    {
        var rng = Random.Shared;
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public Task SaveRegionAsync(
        Guid ampSubscriptionId,
        Guid tenantId,
        string azureRegion,
        string actorUpn,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(azureRegion))
        {
            throw new ArgumentException("azureRegion is required", nameof(azureRegion));
        }

        var consent = this.consentRepo.GetByAmpSubscriptionId(ampSubscriptionId)
                      ?? new SubscriptionTenantConsent
                      {
                          AmpSubscriptionId = ampSubscriptionId,
                          TenantId = tenantId,
                      };

        var now = DateTime.UtcNow;
        consent.TenantId = tenantId;
        consent.AzureRegion = azureRegion;
        consent.AzureRegionSelectedUtc = now;
        consent.AzureRegionSelectedByUpn = actorUpn;

        // Detected region: resolving IS the fan-out completion. The daily ratification (reconcile)
        // job is the authority that propagates the TenantRegions row (SubscriptionProvider =
        // MarketplaceSaaS) into every regional database, so we do NOT synchronously call the
        // signaling endpoint here. By the time the installer returns to add sites the daily job
        // will have created the regional rows; marking complete now lets Setup proceed immediately.
        consent.TenantRegionsFanOutCompleteUtc = now;
        consent.FanOutFailureRegions = null;
        this.consentRepo.Save(consent);

        return Task.CompletedTask;
    }

    public async Task<bool> SaveRegionAndFanOutAsync(
        Guid ampSubscriptionId,
        Guid tenantId,
        string azureRegion,
        string actorUpn,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(azureRegion))
        {
            throw new ArgumentException("azureRegion is required", nameof(azureRegion));
        }

        var consent = this.consentRepo.GetByAmpSubscriptionId(ampSubscriptionId)
                      ?? new SubscriptionTenantConsent
                      {
                          AmpSubscriptionId = ampSubscriptionId,
                          TenantId = tenantId,
                      };

        var now = DateTime.UtcNow;
        consent.TenantId = tenantId;
        consent.AzureRegion = azureRegion;
        consent.AzureRegionSelectedUtc = now;
        consent.AzureRegionSelectedByUpn = actorUpn;
        // A manually selected region is brand new to the regions, so it is NOT complete until the
        // push is confirmed below. The daily ratification can't be relied on to unblock the
        // installer here (it runs once a day and never marks this row complete).
        consent.TenantRegionsFanOutCompleteUtc = null;
        consent.FanOutFailureRegions = null;
        this.consentRepo.Save(consent);

        // Look up the AmpplanId for this subscription so the downstream consumer (Legeris
        // InitialiseSaasTenant) can derive the per-tenant Subscriptions.Status: "free-trial"
        // -> "trial", any other plan id -> "live". Null-tolerant for older subscription rows
        // that pre-date AmpplanId capture; consumer defaults to "live" with a warning log.
        var subscription = this.subscriptionsRepo.GetById(ampSubscriptionId);
        var planId = subscription?.AmpplanId;

        // Immediate push: call the fan-out (signaling) endpoint synchronously and wait. Only a
        // confirmed delivery marks the step complete; otherwise the caller surfaces a retry.
        var payload = new
        {
            eventType = "TenantRegionFanOut",
            saasSubscriptionId = ampSubscriptionId,
            assignedTenantId = tenantId,
            azureRegion,
            modifiedUtc = now,
            occurredBy = "Accelerator",
            actorUpn,
            planId,
        };
        var entry = new NotificationOutbox
        {
            EventType = "TenantRegionFanOut",
            EventJson = JsonSerializer.Serialize(payload),
            AmpSubscriptionId = ampSubscriptionId,
            IdempotencyKey = $"TenantRegionFanOut|{ampSubscriptionId:N}|{azureRegion}|{now:O}",
            CreatedUtc = now,
            NextAttemptUtc = now,
        };

        DispatchResult result;
        try
        {
            result = await this.dispatcher.TryDispatchAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new DispatchResult { Outcome = DispatchOutcome.Transient, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }

        if (result.Outcome == DispatchOutcome.Delivered)
        {
            consent.TenantRegionsFanOutCompleteUtc = DateTime.UtcNow;
            consent.FanOutFailureRegions = null;
            this.consentRepo.Save(consent);
            return true;
        }

        // Not delivered: leave the region selected (so a retry re-uses it) but not complete.
        consent.FanOutFailureRegions = result.Error;
        this.consentRepo.Save(consent);
        return false;
    }

    private TenantRegionInfo FallbackResponse(string error)
    {
        var selectors = new List<RegionSelector>();
        if (!string.IsNullOrWhiteSpace(this.config.AzureRegionSelectorsFallback))
        {
            try
            {
                selectors = JsonSerializer.Deserialize<List<RegionSelector>>(
                    this.config.AzureRegionSelectorsFallback,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<RegionSelector>();
            }
            catch
            {
                // Leave selectors empty; caller surfaces the empty-list state.
            }
        }
        return new TenantRegionInfo
        {
            AzRegion = "?",
            AzureRegionSelectors = selectors,
            Error = error,
            IsFallback = true,
        };
    }

    private class Function1Response
    {
        public string AzRegion { get; set; }
        public List<RegionSelector> AzureRegionSelectors { get; set; }
        public string Error { get; set; }
    }
}
