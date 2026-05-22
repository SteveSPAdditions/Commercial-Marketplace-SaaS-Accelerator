// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Options;

/// <summary>
/// Portal endpoint settings used by the Dispatcher to call the SaaS Accelerator
/// CustomerSite webhook endpoint. Bound from the "PortalOptions" config section.
/// </summary>
public class PortalOptions
{
    public const string SectionName = "PortalOptions";

    /// <summary>Base URL of the CustomerSite (no trailing slash). E.g. https://rau-portal.azurewebsites.net.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Shared HMAC secret. Treated as a UTF-8 string key to match the existing Legeris signing convention.</summary>
    public string HmacSecret { get; set; } = string.Empty;

    /// <summary>Per-request HTTP timeout in seconds. Kept short so a stuck portal does not block message lock renewal.</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>The path the portal exposes for buffered webhook deliveries.</summary>
    public string WebhookPath { get; set; } = "/api/AzureWebhook";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(this.BaseUrl))
        {
            throw new InvalidOperationException("PortalOptions:BaseUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(this.HmacSecret))
        {
            throw new InvalidOperationException("PortalOptions:HmacSecret is required.");
        }
    }
}
