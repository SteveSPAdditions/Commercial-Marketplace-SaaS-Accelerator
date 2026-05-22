// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Options;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Test;

[TestClass]
public class PortalClientTests
{
    private const string Secret = "ZmFrZS1zZWNyZXQtZm9yLXVuaXQtdGVzdHM=";

    [TestMethod]
    public async Task PostWebhookAsync_SignsBodyAndSendsExpectedHeaders()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://portal.example/") };
        var options = Microsoft.Extensions.Options.Options.Create(new PortalOptions
        {
            BaseUrl = "https://portal.example",
            HmacSecret = Secret,
            TimeoutSeconds = 8,
            WebhookPath = "/api/AzureWebhook",
        });

        var client = new PortalClient(http, options);
        const string body = "{\"id\":\"op-123\"}";
        using var resp = await client.PostWebhookAsync(body, "op-123", "act-abc", CancellationToken.None);

        Assert.AreEqual(HttpMethod.Post, handler.CapturedMethod);
        Assert.AreEqual("/api/AzureWebhook", handler.CapturedUri!.AbsolutePath);

        var expectedSignature = "sha256=" + HmacSigner.ComputeSignature(body, Secret);
        Assert.AreEqual(expectedSignature, handler.CapturedHeaders["X-Signature"].First());
        Assert.AreEqual("op-123", handler.CapturedHeaders["X-Idempotency-Key"].First());
        Assert.AreEqual("act-abc", handler.CapturedHeaders["X-Receiver-Activity-Id"].First());
        Assert.AreEqual("Buffer", handler.CapturedHeaders["X-Webhook-Source"].First());
        Assert.AreEqual(body, handler.CapturedBody);
    }

    [TestMethod]
    public async Task PostWebhookAsync_ForwardsBodyBytesVerbatim()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://portal.example/") };
        var options = Microsoft.Extensions.Options.Options.Create(new PortalOptions
        {
            BaseUrl = "https://portal.example",
            HmacSecret = Secret,
        });

        var client = new PortalClient(http, options);
        const string body = "{\n  \"id\": \"op-1\",\n  \"unicode\": \"\\u00e9\\u00e8\"\n}";
        using var resp = await client.PostWebhookAsync(body, "op-1", "act-1", CancellationToken.None);

        Assert.AreEqual(body, handler.CapturedBody);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;

        public HttpMethod? CapturedMethod { get; private set; }
        public Uri? CapturedUri { get; private set; }
        public string? CapturedBody { get; private set; }
        public Dictionary<string, IEnumerable<string>> CapturedHeaders { get; } = new();

        public CapturingHandler(HttpStatusCode status)
        {
            this.status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            this.CapturedMethod = request.Method;
            this.CapturedUri = request.RequestUri;
            this.CapturedBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(ct)
                : null;

            foreach (var header in request.Headers)
            {
                this.CapturedHeaders[header.Key] = header.Value.ToList();
            }

            return new HttpResponseMessage(this.status);
        }
    }
}
