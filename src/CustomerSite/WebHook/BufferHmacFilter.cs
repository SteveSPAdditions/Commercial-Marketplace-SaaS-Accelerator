// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Marketplace.SaaS.Accelerator.CustomerSite.WebHook;

/// <summary>
/// Resource filter that verifies the X-Signature header on inbound webhook calls coming
/// from the WebhookBuffer Function App. Re-reads the raw request body (no MVC model
/// binding has happened yet at this stage) and HMACs it with the shared secret. On
/// mismatch, short-circuits with 401 before the controller action runs.
///
/// Only enforces when:
///   - SaaSApiConfiguration.WebhookBufferHmacSecret is configured (non-empty), AND
///   - the inbound request carries X-Webhook-Source: Buffer header.
///
/// Without the source header the filter falls through to the existing JWT-based auth
/// path, preserving compatibility with direct Microsoft posts during a cutover window.
/// </summary>
public class BufferHmacFilter : IAsyncResourceFilter
{
    private readonly SaaSApiClientConfiguration configuration;

    public BufferHmacFilter(SaaSApiClientConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var source = context.HttpContext.Request.Headers["X-Webhook-Source"].ToString();
        if (!string.Equals(source, "Buffer", StringComparison.OrdinalIgnoreCase))
        {
            // Not from the buffer — let the existing JWT-token path handle it.
            await next();
            return;
        }

        var secret = this.configuration?.WebhookBufferHmacSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            // Buffer is in use but portal is not configured. Refuse rather than silently
            // accepting unsigned traffic from a source that claims to be the buffer.
            context.Result = new UnauthorizedObjectResult(new { error = "WebhookBufferHmacSecret not configured" });
            return;
        }

        var presented = context.HttpContext.Request.Headers["X-Signature"].ToString();
        const string prefix = "sha256=";
        if (string.IsNullOrEmpty(presented) || !presented.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "missing or malformed X-Signature" });
            return;
        }

        var presentedHex = presented.Substring(prefix.Length);

        // Read the raw body into memory so we can both HMAC-verify it AND give MVC's
        // [FromBody] binder a fresh, seekable stream to bind from. Replacing Request.Body
        // with a new MemoryStream avoids the FileBufferingReadStream / [ApiController]
        // re-read interactions that have bitten ASP.NET Core 8 in similar setups.
        byte[] bodyBytes;
        using (var ms = new MemoryStream())
        {
            await context.HttpContext.Request.Body.CopyToAsync(ms).ConfigureAwait(false);
            bodyBytes = ms.ToArray();
        }
        var rawBody = System.Text.Encoding.UTF8.GetString(bodyBytes);

        if (!HmacSigner.Verify(rawBody, secret, presentedHex))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "signature mismatch" });
            return;
        }

        // Replace the request body with a fresh MemoryStream the binder can read.
        context.HttpContext.Request.Body = new MemoryStream(bodyBytes);
        context.HttpContext.Request.ContentLength = bodyBytes.LongLength;

        await next();
    }
}
