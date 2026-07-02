// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Builds and validates admin-consent redirects for the Read and Understood
/// runtime Entra application.
/// </summary>
public interface ITenantAdminConsentService
{
    /// <summary>
    /// Construct the Microsoft /adminconsent URL with a signed state token
    /// that ties the callback back to the subscription.
    /// </summary>
    string BuildConsentUrl(Guid tenantId, Guid ampSubscriptionId, string callbackUri);

    /// <summary>
    /// Validate the state signature returned by Microsoft on the callback.
    /// Returns the subscriptionId encoded in state, or null if invalid/expired.
    /// </summary>
    Guid? ValidateCallbackState(string state);

    /// <summary>Mark consent as granted on the SubscriptionTenantConsent row.</summary>
    Task RecordConsentAsync(
        Guid ampSubscriptionId,
        string upn,
        string objectId,
        CancellationToken ct);

    /// <summary>
    /// Construct the /adminconsent URL for the shared Acknowledge Teams app (Setup step 5).
    /// Same signed-state choreography as <see cref="BuildConsentUrl"/> but targets the
    /// TeamsActivityAppClientId app (TeamsActivity.Send).
    /// </summary>
    string BuildTeamsActivityConsentUrl(Guid tenantId, Guid ampSubscriptionId, string callbackUri);

    /// <summary>Mark Teams-activity-app consent as granted on the SubscriptionTenantConsent row.</summary>
    Task RecordTeamsActivityConsentAsync(
        Guid ampSubscriptionId,
        string upn,
        string objectId,
        CancellationToken ct);
}
