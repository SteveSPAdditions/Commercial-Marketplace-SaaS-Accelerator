// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Dispatches outbox rows to the Legeris EUSA signaling endpoint with HMAC-SHA256
/// signing. Classifies response into Delivered / Transient / Permanent so the
/// drain loop can pick the right retry policy.
/// </summary>
public class LegerisSignalingDispatcher : IOutboxDispatcher
{
    private readonly HttpClient httpClient;
    private readonly SaaSApiClientConfiguration config;

    public LegerisSignalingDispatcher(HttpClient httpClient, SaaSApiClientConfiguration config)
    {
        this.httpClient = httpClient;
        this.config = config;
    }

    public async Task<DispatchResult> TryDispatchAsync(NotificationOutbox entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(this.config.LegerisSignalingEndpointUrl))
        {
            // Not configured = nothing to deliver. Permanent (won't ever succeed)
            // so the drain loop dead-letters rather than retries forever.
            return new DispatchResult
            {
                Outcome = DispatchOutcome.Permanent,
                Error = "LegerisSignalingEndpointUrl is not configured",
            };
        }

        var body = entry.EventJson ?? string.Empty;
        var signature = SignBody(body);

        using var req = new HttpRequestMessage(HttpMethod.Post, this.config.LegerisSignalingEndpointUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Signature", $"sha256={signature}");
        req.Headers.TryAddWithoutValidation("X-Event-Type", entry.EventType ?? "Unknown");
        req.Headers.TryAddWithoutValidation("X-Idempotency-Key", entry.IdempotencyKey ?? string.Empty);

        try
        {
            using var resp = await this.httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var snippet = await ReadSnippetAsync(resp, ct).ConfigureAwait(false);
            return ClassifyResponse(resp.StatusCode, snippet);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return new DispatchResult
            {
                Outcome = DispatchOutcome.Transient,
                Error = "Request timeout",
                ResponseSnippet = ex.Message,
            };
        }
        catch (HttpRequestException ex)
        {
            return new DispatchResult
            {
                Outcome = DispatchOutcome.Transient,
                Error = $"HTTP error: {ex.Message}",
            };
        }
        catch (IOException ex)
        {
            return new DispatchResult
            {
                Outcome = DispatchOutcome.Transient,
                Error = $"IO error: {ex.Message}",
            };
        }
    }

    private string SignBody(string body)
    {
        var key = this.config.LegerisSignalingHmacSecret ?? string.Empty;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static DispatchResult ClassifyResponse(HttpStatusCode status, string snippet)
    {
        var code = (int)status;
        if (code >= 200 && code < 300)
        {
            return new DispatchResult { Outcome = DispatchOutcome.Delivered, ResponseSnippet = snippet };
        }
        // 409 with idempotent-duplicate body is success
        if (code == 409)
        {
            return new DispatchResult { Outcome = DispatchOutcome.Delivered, ResponseSnippet = snippet };
        }
        // 408 timeout, 429 throttle, 5xx — retry
        if (code == 408 || code == 429 || (code >= 500 && code < 600))
        {
            return new DispatchResult
            {
                Outcome = DispatchOutcome.Transient,
                Error = $"HTTP {code}",
                ResponseSnippet = snippet,
            };
        }
        // 4xx other than above — dead-letter
        return new DispatchResult
        {
            Outcome = DispatchOutcome.Permanent,
            Error = $"HTTP {code}",
            ResponseSnippet = snippet,
        };
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return content == null
                ? null
                : (content.Length <= 512 ? content : content.Substring(0, 512));
        }
        catch
        {
            return null;
        }
    }
}
