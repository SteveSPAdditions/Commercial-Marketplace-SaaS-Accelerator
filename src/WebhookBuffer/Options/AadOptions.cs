// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Options;

/// <summary>
/// Settings for validating the Microsoft Marketplace-issued JWT presented on inbound
/// webhook calls. Bound from the "AadOptions" config section. Mirrors the validation
/// parameters used by Services.Utilities.ValidateJwtToken in the portal.
/// </summary>
public class AadOptions
{
    public const string SectionName = "AadOptions";

    /// <summary>Microsoft AAD tenant ID expected on the token. Matches portal SaaSApiConfiguration:TenantId.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Expected JWT 'aud' claim — the SPP / fulfillment app's clientId (e.g. 252c2797-...). This is the
    /// Microsoft Entra Identity application registered against the offer in Partner Center. The webhook JWT
    /// is signed FOR this app. Matches portal SaaSApiConfiguration:ClientId.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Expected JWT 'azp'/'appid' claim — the Microsoft Marketplace service's caller id. Constant for the
    /// public cloud: 20e940b3-4c77-4b0b-9a53-9e16a1b010a7. Matches portal SaaSApiConfiguration:Resource.
    /// The JWT is signed BY this caller.
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(this.TenantId))
        {
            throw new InvalidOperationException("AadOptions:TenantId is required.");
        }

        if (string.IsNullOrWhiteSpace(this.ClientId))
        {
            throw new InvalidOperationException("AadOptions:ClientId is required (the SPP / fulfillment app clientId).");
        }

        if (string.IsNullOrWhiteSpace(this.Resource))
        {
            throw new InvalidOperationException("AadOptions:Resource is required (the Marketplace caller id, 20e940b3-... for public cloud).");
        }
    }
}
