// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Utilities;

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
        var signature = HmacSigner.ComputeSignature(body, this.config.LegerisSignalingHmacSecret);

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
            return ClassifyResponse(resp, snippet);
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

    private static DispatchResult ClassifyResponse(HttpResponseMessage resp, string snippet)
    {
        var code = (int)resp.StatusCode;
        if (code >= 200 && code < 300)
        {
            return new DispatchResult { Outcome = DispatchOutcome.Delivered, ResponseSnippet = snippet };
        }
        // 409 with idempotent-duplicate body is success
        if (code == 409)
        {
            return new DispatchResult { Outcome = DispatchOutcome.Delivered, ResponseSnippet = snippet };
        }

        // 3xx — the request never reached the receiver. Redirect-following is DISABLED on this
        // client for exactly this reason: the receiver sits behind OWIN/OIDC auth, which converts
        // its 401 into a 302 to login.microsoftonline.com. Followed, that chain ends at the Entra
        // sign-in page -- HTTP 200 -- which we would have banked as Delivered and deleted the row.
        // Treat as transient (an auth/config fault that clears when fixed) and surface the target.
        if (code >= 300 && code < 400)
        {
            var location = resp.Headers.Location?.ToString();
            return new DispatchResult
            {
                Outcome = DispatchOutcome.Transient,
                Error = $"HTTP {code} redirect (endpoint is behind an auth challenge; request never reached the receiver)"
                        + (string.IsNullOrEmpty(location) ? string.Empty : $" -> {location}"),
                ResponseSnippet = snippet,
            };
        }

        // 404 — retry rather than dead-letter. A 404 here is far more often "the endpoint isn't
        // there RIGHT NOW" than "the endpoint will never exist": an ngrok tunnel whose agent has
        // dropped answers 404 (ERR_NGROK_3200) on the still-resolvable domain, and a receiver
        // mid-deploy does the same. Per the sender contract, a condition that can clear on its own
        // must not be a zero-retry dead-letter. A genuinely wrong URL still dead-letters -- it just
        // takes the full backoff ladder to get there.
        // 408 timeout, 429 throttle, 5xx — retry
        if (code == 404 || code == 408 || code == 429 || (code >= 500 && code < 600))
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
