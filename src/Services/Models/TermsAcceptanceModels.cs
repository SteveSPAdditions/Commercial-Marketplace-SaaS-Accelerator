// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.Services.Models;

/// <summary>One of the two documents the customer must agree to: title, link and version label.</summary>
public class TermsDocument
{
    public string Title { get; set; }

    /// <summary>Absolute URL, or a site-relative path (leading '/') served by the customer portal.</summary>
    public string Url { get; set; }

    /// <summary>Free-text version label (e.g. "1.0" or "2026-09-01"). May be empty.</summary>
    public string Version { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(this.Url);
}

/// <summary>
/// Evaluated terms-gate state for one subscription. <see cref="IsSatisfied"/> is the single answer
/// callers need: false means Setup steps must stay locked and mutating Setup actions must refuse.
/// </summary>
public class TermsAcceptanceStatus
{
    public Guid AmpSubscriptionId { get; set; }

    /// <summary>The TermsAcceptanceRequired kill switch. False disables the gate entirely.</summary>
    public bool Required { get; set; }

    /// <summary>Both document URLs are present in ApplicationConfiguration.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>An acceptance row exists for this subscription.</summary>
    public bool Accepted { get; set; }

    /// <summary>The most recent acceptance row, or null.</summary>
    public SubscriptionTermsAcceptance Acceptance { get; set; }

    public TermsDocument MicrosoftContract { get; set; }

    public TermsDocument PublisherAmendment { get; set; }

    /// <summary>
    /// True when the gate does not block Setup: either it is switched off, or the subscription has
    /// an acceptance on record. A required-but-unconfigured gate is NOT satisfied (fail closed).
    /// </summary>
    public bool IsSatisfied => !this.Required || this.Accepted;

    /// <summary>Required, no acceptance yet, and the documents are not configured: the step cannot be actioned.</summary>
    public bool IsMisconfigured => this.Required && !this.Accepted && !this.IsConfigured;
}

/// <summary>What the Setup POST supplies when the customer accepts.</summary>
public class TermsAcceptanceRequest
{
    public bool MicrosoftContractAccepted { get; set; }

    public bool PublisherAmendmentAccepted { get; set; }

    public string AcceptedByUpn { get; set; }

    public string AcceptedByObjectId { get; set; }

    public string AcceptedByDisplayName { get; set; }

    public string IpAddress { get; set; }

    public string UserAgent { get; set; }

    /// <summary>Where the acceptance was captured; defaults to "Setup".</summary>
    public string Source { get; set; } = "Setup";
}

/// <summary>Outcome of <see cref="Contracts.ITermsAcceptanceService.Record"/>.</summary>
public class TermsAcceptanceResult
{
    public bool Success { get; set; }

    /// <summary>Customer-facing reason when <see cref="Success"/> is false.</summary>
    public string Error { get; set; }

    public SubscriptionTermsAcceptance Acceptance { get; set; }

    public static TermsAcceptanceResult Fail(string error) => new() { Success = false, Error = error };
}
