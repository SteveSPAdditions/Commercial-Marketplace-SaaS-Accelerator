// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Options;
using Microsoft.Extensions.Options;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Services;

/// <summary>
/// Typed HttpClient that signs the raw body with HMAC-SHA256 and POSTs to the portal's
/// webhook endpoint. The body is sent byte-for-byte as supplied — the portal re-signs
/// the bytes it receives, so any post-processing here would break verification.
/// </summary>
public class PortalClient : IPortalClient
{
    private readonly HttpClient httpClient;
    private readonly PortalOptions options;

    public PortalClient(HttpClient httpClient, IOptions<PortalOptions> options)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
    }

    public async Task<HttpResponseMessage> PostWebhookAsync(string rawBody, string operationId, string activityId, CancellationToken ct)
    {
        var signature = HmacSigner.ComputeSignature(rawBody, this.options.HmacSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, this.options.WebhookPath)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Signature", $"sha256={signature}");
        request.Headers.TryAddWithoutValidation("X-Idempotency-Key", operationId ?? string.Empty);
        request.Headers.TryAddWithoutValidation("X-Receiver-Activity-Id", activityId ?? string.Empty);
        request.Headers.TryAddWithoutValidation("X-Webhook-Source", "Buffer");

        return await this.httpClient.SendAsync(request, ct).ConfigureAwait(false);
    }
}
