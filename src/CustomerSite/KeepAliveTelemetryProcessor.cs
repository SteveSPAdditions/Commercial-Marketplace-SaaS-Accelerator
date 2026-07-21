// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;

namespace Marketplace.SaaS.Accelerator.CustomerSite;

/// <summary>
/// Drops request telemetry produced by Azure App Service "Always On" keep-alive pings
/// (User-Agent "AlwaysOn") so they don't dilute full-fidelity activity logging. Adaptive
/// sampling is disabled site-wide, so without this filter every keep-alive ping would be
/// recorded. All other telemetry is passed through untouched.
/// </summary>
public class KeepAliveTelemetryProcessor : ITelemetryProcessor
{
    private readonly ITelemetryProcessor next;
    private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeepAliveTelemetryProcessor"/> class.
    /// <paramref name="next"/> is supplied by the Application Insights processor-chain
    /// factory; <paramref name="httpContextAccessor"/> is resolved from DI.
    /// </summary>
    public KeepAliveTelemetryProcessor(ITelemetryProcessor next, IHttpContextAccessor httpContextAccessor)
    {
        this.next = next;
        this.httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc/>
    public void Process(ITelemetry item)
    {
        if (item is RequestTelemetry && this.IsAlwaysOnPing())
        {
            return; // Swallow the keep-alive request; do not forward it down the chain.
        }

        this.next.Process(item);
    }

    private bool IsAlwaysOnPing()
    {
        var userAgent = this.httpContextAccessor.HttpContext?.Request?.Headers["User-Agent"].ToString();
        return !string.IsNullOrEmpty(userAgent)
            && userAgent.IndexOf("AlwaysOn", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
