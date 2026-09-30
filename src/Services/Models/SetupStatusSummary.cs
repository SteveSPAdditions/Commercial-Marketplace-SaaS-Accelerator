// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.Services.Models;

/// <summary>
/// Lightweight status of the post-acceptance Setup flow for one subscription,
/// suitable for rendering a pill or dropdown entry on the subscriptions list.
/// </summary>
public class SetupStatusSummary
{
    public Guid AmpSubscriptionId { get; set; }

    /// <summary>The terms gate is switched on (TermsAcceptanceRequired). Adds a step to the checklist.</summary>
    public bool TermsRequired { get; set; }

    /// <summary>The Marketplace terms + publisher amendment have been accepted for this subscription.</summary>
    public bool TermsAccepted { get; set; }

    /// <summary>Region row exists with a non-null AzureRegion.</summary>
    public bool RegionSelected { get; set; }

    /// <summary>Fan-out to all regional Function1 instances has completed.</summary>
    public bool RegionFanOutComplete { get; set; }

    /// <summary>Tenant admin has granted consent to the runtime app.</summary>
    public bool TenantConsented { get; set; }

    /// <summary>Tenant admin has granted consent to the shared Acknowledge Teams app (TeamsActivity.Send).</summary>
    public bool TeamsActivityConsented { get; set; }

    /// <summary>At least one SharePoint site has been enrolled.</summary>
    public bool HasSites { get; set; }

    /// <summary>1..TotalSteps — Step 1 is always counted complete when this summary is produced.</summary>
    public int CompletedSteps { get; set; }

    /// <summary>Five provisioning steps, plus the terms step when the gate is on.</summary>
    public int TotalSteps => this.TermsRequired ? 6 : 5;

    public bool IsComplete => this.CompletedSteps >= this.TotalSteps;

    /// <summary>Relative URL to the subscriber-facing Setup page.</summary>
    public string SetupUrl { get; set; }
}
