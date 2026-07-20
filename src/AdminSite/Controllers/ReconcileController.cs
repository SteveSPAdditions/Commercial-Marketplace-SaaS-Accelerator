// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketplace.SaaS.Accelerator.AdminSite.Controllers;

/// <summary>
/// Read-only snapshot endpoint consumed by each Legeris region's daily
/// SaaSInitialiseTenantRegions reconciliation. HMAC-authenticated, no user
/// identity. Returns the full tenant->region directory for every Marketplace
/// subscription that has reached Step 2 (region selected). Each Legeris instance
/// pulls the same payload and filters locally against its own MDB.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/saasaccelerator")]
public class ReconcileController : ControllerBase
{
    private const int TimestampSkewSeconds = 300; // 5 min replay window
    private const string SignaturePath = "/api/saasaccelerator/reconcile-snapshot";

    private readonly SaasKitContext context;
    private readonly SaaSApiClientConfiguration config;
    private readonly SaaSClientLogger<ReconcileController> logger;

    public ReconcileController(
        SaasKitContext context,
        SaaSApiClientConfiguration config,
        SaaSClientLogger<ReconcileController> logger)
    {
        this.context = context;
        this.config = config;
        this.logger = logger;
    }

    [HttpGet("reconcile-snapshot")]
    public IActionResult ReconcileSnapshot()
    {
        var authError = this.VerifyHmac();
        if (authError != null)
        {
            return authError;
        }

        try
        {
            // Source of truth for the per-region tenant directory. No SubscriptionStatus
            // filter -- MDBs keep TenantRegion rows for Unsubscribed/Suspended tenants too.
            // No region filter -- each MDB tracks the full directory and filters locally.
            var rows = (from stc in this.context.SubscriptionTenantConsent
                        join s in this.context.Subscriptions
                            on stc.AmpSubscriptionId equals s.AmpsubscriptionId
                        where stc.AzureRegion != null
                        select new
                        {
                            purchaserTenantId = stc.TenantId,
                            azureRegion = stc.AzureRegion,
                            ampSubscriptionId = stc.AmpSubscriptionId,
                            subscriptionStatus = s.SubscriptionStatus,
                            modifiedUtc = stc.ModifiedUtc,
                        })
                .ToList()
                .Select(r => new
                {
                    r.purchaserTenantId,
                    r.azureRegion,
                    r.ampSubscriptionId,
                    // Normalize to Microsoft canonical (Suspend -> Suspended) so RAU's
                    // push-authoritative reconcile corrector compares against the same
                    // vocabulary the live Fulfillment pull returns.
                    subscriptionStatus = SubscriptionStatusNormalizer.ToMarketplaceStatus(r.subscriptionStatus),
                    r.modifiedUtc,
                })
                .ToList();

            return this.Ok(new
            {
                generatedUtc = DateTime.UtcNow,
                count = rows.Count,
                complete = true,
                tenants = rows,
            });
        }
        catch (Exception ex)
        {
            this.logger.LogError($"ReconcileSnapshot query failed: {ex.Message}");
            // Return 500 with complete=false so the reconciler still applies INSERT/UPDATE
            // from any partial body but skips the DELETE phase.
            return this.StatusCode(500, new
            {
                generatedUtc = DateTime.UtcNow,
                count = 0,
                complete = false,
                error = ex.Message,
            });
        }
    }

    private IActionResult VerifyHmac()
    {
        var providedSig = (string)this.Request.Headers["X-Signature"] ?? string.Empty;
        var providedTs = (string)this.Request.Headers["X-Signature-Timestamp"] ?? string.Empty;

        if (string.IsNullOrEmpty(providedSig) || string.IsNullOrEmpty(providedTs))
        {
            return this.Unauthorized("Missing X-Signature or X-Signature-Timestamp");
        }

        if (!long.TryParse(providedTs, out var ts))
        {
            return this.Unauthorized("Invalid timestamp");
        }

        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(nowEpoch - ts) > TimestampSkewSeconds)
        {
            return this.Unauthorized("Timestamp out of window");
        }

        var secret = this.config.LegerisSignalingHmacSecret;
        if (string.IsNullOrEmpty(secret))
        {
            // Refuse to accept anything if the secret isn't configured. Treating an
            // unset secret as "valid" would silently disable authentication.
            return this.Unauthorized("Server secret not configured");
        }

        var canonical = "GET\n" + SignaturePath + "\n" + providedTs;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var expected = Convert.ToHexString(bytes).ToLowerInvariant();

        var provided = providedSig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? providedSig.Substring("sha256=".Length).ToLowerInvariant()
            : providedSig.ToLowerInvariant();

        if (provided.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(provided),
                Encoding.ASCII.GetBytes(expected)))
        {
            return this.Unauthorized();
        }

        return null;
    }
}
