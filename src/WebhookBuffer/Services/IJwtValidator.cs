// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System.Threading;
using System.Threading.Tasks;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Services;

public interface IJwtValidator
{
    /// <summary>
    /// Validates the JWT signature, lifetime, audience, and tenant/azp claims. Returns
    /// true if the token is acceptable; throws on signing-key fetch failures (transient
    /// — caller maps to 503).
    /// </summary>
    Task<bool> ValidateAsync(string token, CancellationToken ct);
}
