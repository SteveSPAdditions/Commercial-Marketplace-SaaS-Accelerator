// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Implements the /adminconsent redirect-and-return choreography for the
/// Read and Understood runtime Entra app. State token is HMAC-signed with the
/// signaling secret so we can correlate the callback without trusting
/// query-string contents.
///
/// Scope dependency (AppAddin2 Verification Gate Phase 3, brief §8): the runtime
/// app's Tenant.Lookup delegated scope is consumed by the SPFx GetSetupState()
/// helper. v1 /adminconsent grants tenant-wide consent to every permission
/// currently registered on the runtime app, so as long as Tenant.Lookup is
/// registered on the app BEFORE Step 3 runs, the SPFx caller will get tokens
/// carrying it in `scp` without a per-user prompt. Tenants that completed
/// Step 3 BEFORE the scope was added will see one incremental admin-consent
/// prompt on the first SPFx call (open item 6 in the brief).
/// </summary>
public class TenantAdminConsentService : ITenantAdminConsentService
{
    private const int StateValiditySeconds = 600; // 10 minutes

    private readonly SaaSApiClientConfiguration config;
    private readonly ISubscriptionTenantConsentRepository consentRepo;

    public TenantAdminConsentService(
        SaaSApiClientConfiguration config,
        ISubscriptionTenantConsentRepository consentRepo)
    {
        this.config = config;
        this.consentRepo = consentRepo;
    }

    public string BuildConsentUrl(Guid tenantId, Guid ampSubscriptionId, string callbackUri)
    {
        // RuntimeAppClientId is the Entra app for the post-acceptance RUNTIME (e.g. Read and
        // Understood). It must be a different app from MTClientId (portal sign-in) -- by the
        // time the customer reaches Step 3 they're already signed in, so falling back to
        // MTClientId here would prompt them to consent to the sign-in app a second time,
        // which is meaningless. Hard-fail if RuntimeAppClientId isn't configured.
        if (string.IsNullOrWhiteSpace(this.config.RuntimeAppClientId))
        {
            throw new InvalidOperationException("RuntimeAppClientId is not configured");
        }

        return this.BuildAdminConsentUrl(this.config.RuntimeAppClientId, tenantId, ampSubscriptionId, callbackUri);
    }

    public string BuildTeamsActivityConsentUrl(Guid tenantId, Guid ampSubscriptionId, string callbackUri)
    {
        // TeamsActivityAppClientId is the shared Acknowledge Teams app (TeamsActivity.Send). This is
        // a SECOND admin consent, separate from the runtime app -- mandatory step 5, since Teams
        // activity notifications can't be sent without it. One shared app across ZoHo + SaaS.
        if (string.IsNullOrWhiteSpace(this.config.TeamsActivityAppClientId))
        {
            throw new InvalidOperationException("TeamsActivityAppClientId is not configured");
        }

        return this.BuildAdminConsentUrl(this.config.TeamsActivityAppClientId, tenantId, ampSubscriptionId, callbackUri);
    }

    private string BuildAdminConsentUrl(string clientId, Guid tenantId, Guid ampSubscriptionId, string callbackUri)
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $"{ampSubscriptionId:N}.{issued}";
        var signature = Sign(payload);
        var state = $"{payload}.{signature}";

        var authority = string.IsNullOrEmpty(this.config.AdAuthenticationEndPoint)
            ? "https://login.microsoftonline.com"
            : this.config.AdAuthenticationEndPoint.TrimEnd('/');

        return $"{authority}/{tenantId}/adminconsent"
               + $"?client_id={Uri.EscapeDataString(clientId)}"
               + $"&redirect_uri={Uri.EscapeDataString(callbackUri)}"
               + $"&state={Uri.EscapeDataString(state)}";
    }

    public Guid? ValidateCallbackState(string state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        var parts = state.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var payload = $"{parts[0]}.{parts[1]}";
        var expected = Sign(payload);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(parts[2])))
        {
            return null;
        }

        if (!long.TryParse(parts[1], out var issued))
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issued;
        if (age < 0 || age > StateValiditySeconds)
        {
            return null;
        }

        if (!Guid.TryParseExact(parts[0], "N", out var subscriptionId))
        {
            return null;
        }
        return subscriptionId;
    }

    public Task RecordConsentAsync(Guid ampSubscriptionId, string upn, string objectId, CancellationToken ct)
    {
        var consent = this.consentRepo.GetByAmpSubscriptionId(ampSubscriptionId)
                      ?? new SubscriptionTenantConsent { AmpSubscriptionId = ampSubscriptionId };

        consent.RuntimeAppConsentedUtc = DateTime.UtcNow;
        consent.ConsentedByUpn = upn;
        consent.ConsentedByObjectId = objectId;
        this.consentRepo.Save(consent);
        return Task.CompletedTask;
    }

    public Task RecordTeamsActivityConsentAsync(Guid ampSubscriptionId, string upn, string objectId, CancellationToken ct)
    {
        var consent = this.consentRepo.GetByAmpSubscriptionId(ampSubscriptionId)
                      ?? new SubscriptionTenantConsent { AmpSubscriptionId = ampSubscriptionId };

        consent.TeamsActivityAppConsentedUtc = DateTime.UtcNow;
        consent.TeamsActivityConsentedByUpn = upn;
        consent.TeamsActivityConsentedByObjectId = objectId;
        this.consentRepo.Save(consent);
        return Task.CompletedTask;
    }

    private string Sign(string payload)
    {
        var secret = this.config.LegerisSignalingHmacSecret ?? string.Empty;
        // Even when the signaling secret is empty (dev), produce a deterministic
        // hash so state still round-trips; callers should configure a real secret
        // in any environment that matters.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
