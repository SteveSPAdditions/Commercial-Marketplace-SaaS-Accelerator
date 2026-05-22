// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.
using System;

namespace Marketplace.SaaS.Accelerator.Services.Configurations;

/// <summary>
/// Fulfillment Client Configuration.
/// </summary>
public class SaaSApiClientConfiguration
{
    /// <summary>
    /// Gets or sets the type of the grant.
    /// </summary>
    /// <value>
    /// The type of the grant.
    /// </value>
    public string GrantType { get; set; }

    /// <summary>
    /// Gets or sets the client identifier.
    /// </summary>
    /// <value>
    /// The client identifier.
    /// </value>
    public string ClientId { get; set; }

    /// <summary>
    /// Gets or sets the client secret.
    /// </summary>
    /// <value>
    /// The client secret.
    /// </value>
    public string ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the AAD Client ID resource.
    /// </summary>
    /// <value>
    /// The resource.
    /// </value>
    public string MTClientId { get; set; }

    /// <summary>
    /// Gets or sets the resource.
    /// </summary>
    /// <value>
    /// The resource.
    /// </value>

    public string Resource { get; set; }

    /// <summary>
    /// Gets or sets the base URL.
    /// </summary>
    /// <value>
    /// The base URL.
    /// </value>
    public string FulFillmentAPIBaseURL { get; set; }

    /// <summary>
    /// Gets or sets the signed out redirect URI.
    /// </summary>
    /// <value>
    /// The signed out redirect URI.
    /// </value>
    public string SignedOutRedirectUri { get; set; }

    /// <summary>
    /// Gets or sets the tenant identifier.
    /// </summary>
    /// <value>
    /// The tenant identifier.
    /// </value>
    public string TenantId { get; set; }

    /// <summary>
    /// Gets or sets the API version.
    /// </summary>
    /// <value>
    /// The API version.
    /// </value>
    public string FulFillmentAPIVersion { get; set; }

    /// <summary>
    /// Gets or sets the Authentication end point.
    /// </summary>
    /// <value>
    /// The Authentication end point.
    /// </value>
    public string AdAuthenticationEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the saa s application URL.
    /// </summary>
    /// <value>
    /// The saas application URL.
    /// </value>
    public string SaaSAppUrl { get; set; }

    /// <summary>
    /// Initializes or Gets the current run environment. Set to "development" or "production" is assumed.
    /// </summary>
    /// <value>
    /// The production-level environment. Typically, "development", "production", or null.
    /// </value>
    public string Environment { get; init; }
    /// <summary>
    /// Initializes or Gets the value for IsAdminPortalMultiTenant. Set to true or false is assumed.
    /// </summary>
    /// <value>
    /// The value for IsAdminPortalMultiTenant. Typically, true, false, or null.
    /// </value>
    public string IsAdminPortalMultiTenant { get; set; }

    // --- Read and Understood post-acceptance setup UX ---

    /// <summary>Legacy single-region Function1 endpoint. Used only when AzRegionSvcUrlTemplate is empty.</summary>
    public string AzRegionSvcUrl { get; set; }

    /// <summary>
    /// URL template for the AzRSvc Function1 endpoint with a literal "{region}" placeholder, e.g.
    /// "https://readandunderstoodazrsvc-{region}.azurewebsites.net/api/Function1". The service substitutes
    /// each entry of <see cref="AzRegionSvcRegions"/> in turn, shuffled, until one returns a usable answer.
    /// </summary>
    public string AzRegionSvcUrlTemplate { get; set; }

    /// <summary>
    /// Comma-separated AzRSvc region slugs to try (in random order) for tenant-region lookup, e.g. "eusa,uk,ca,au".
    /// Mirrors the azrSvcRegions failover list used by the SPfx shared-functions package.
    /// </summary>
    public string AzRegionSvcRegions { get; set; }

    /// <summary>Legeris EUSA signaling endpoint (POST /api/saasaccelerator/event) for cross-region fan-out.</summary>
    public string LegerisSignalingEndpointUrl { get; set; }

    /// <summary>Pre-shared HMAC-SHA256 secret used to sign events posted to the Legeris signaling endpoint.</summary>
    public string LegerisSignalingHmacSecret { get; set; }

    /// <summary>Pre-shared HMAC-SHA256 secret used to verify inbound webhook calls forwarded by the WebhookBuffer Function App.</summary>
    public string WebhookBufferHmacSecret { get; set; }

    /// <summary>JSON-encoded fallback region selector list used when Function1 is unreachable.</summary>
    public string AzureRegionSelectorsFallback { get; set; }

    /// <summary>Entra app id for the Read and Understood runtime application (consent target + per-site grant subject).</summary>
    public string RuntimeAppClientId { get; set; }

    /// <summary>
    /// Path to the SPP's client-credential certificate (.pfx). Absolute path, or relative
    /// to the app's ContentRoot.
    /// </summary>
    public string MTCertPath { get; set; }

    /// <summary>Password protecting the .pfx file referenced by <see cref="MTCertPath"/>. KV-reference in production.</summary>
    public string MTCertPassword { get; set; }

    /// <summary>Maximum delivery attempts before an outbox row is dead-lettered.</summary>
    public int OutboxMaxAttempts { get; set; } = 12;

    /// <summary>Interval in seconds between outbox drain passes.</summary>
    public int OutboxDrainIntervalSeconds { get; set; } = 30;

    /// <summary>Redirect activations to the new Setup page instead of ProcessMessage.</summary>
    public bool RedirectActivateToSetup { get; set; } = false;
}