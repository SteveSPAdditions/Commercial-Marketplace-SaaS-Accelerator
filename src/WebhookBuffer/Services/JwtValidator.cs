// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Services;

/// <summary>
/// JWT validator mirroring the rules in Services.Utilities.ValidateJwtToken. Caches the
/// OpenID Connect configuration for the lifetime of the singleton (the underlying
/// <see cref="ConfigurationManager{T}"/> refreshes signing keys per its own schedule —
/// default refresh interval 5 minutes, automatic refresh on validation failure).
/// </summary>
public class JwtValidator : IJwtValidator
{
    private readonly AadOptions options;
    private readonly ILogger<JwtValidator> logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> configManager;

    public JwtValidator(IOptions<AadOptions> options, ILogger<JwtValidator> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        var metadataUrl = $"https://login.microsoftonline.com/{this.options.TenantId}/.well-known/openid-configuration";
        this.configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataUrl,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever());
    }

    public async Task<bool> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        OpenIdConnectConfiguration openIdConfig;
        try
        {
            openIdConfig = await this.configManager.GetConfigurationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to fetch AAD OpenID configuration");
            throw;
        }

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = true,
            ValidAudience = this.options.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = openIdConfig.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(token, validationParameters, out _);

            var tid = principal.FindFirst("tid")?.Value
                      ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
            if (!string.Equals(tid, this.options.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                this.logger.LogWarning("JWT rejected: tenantId claim {Tid} does not match configured TenantId", tid);
                return false;
            }

            var azp = principal.FindFirst("azp")?.Value ?? principal.FindFirst("appid")?.Value;
            if (!string.Equals(azp, this.options.Resource, StringComparison.OrdinalIgnoreCase))
            {
                this.logger.LogWarning("JWT rejected: azp/appid claim {Azp} does not match configured Resource (Marketplace caller id)", azp);
                return false;
            }

            return true;
        }
        catch (SecurityTokenException ex)
        {
            this.logger.LogWarning("JWT validation failed: {Message}", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            // ValidateToken throws ArgumentException for tokens that aren't even JWT-shaped
            // (no dots, not base64, etc.). Treat as bad auth, not transient.
            this.logger.LogWarning("JWT malformed: {Message}", ex.Message);
            return false;
        }
    }
}
