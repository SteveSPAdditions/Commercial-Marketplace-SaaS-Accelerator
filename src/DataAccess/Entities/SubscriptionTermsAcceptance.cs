// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.DataAccess.Entities;

/// <summary>
/// One row per terms-acceptance event on the customer portal: who ticked the boxes, when, and the
/// exact document versions and URLs that were presented at the time. Append-only -- rows are never
/// updated or deleted, so the table is the legal record of acceptance for a subscription.
/// </summary>
public partial class SubscriptionTermsAcceptance
{
    public int Id { get; set; }

    public Guid AmpSubscriptionId { get; set; }

    public Guid TenantId { get; set; }

    public DateTime AcceptedUtc { get; set; }

    public string AcceptedByUpn { get; set; }

    public string AcceptedByObjectId { get; set; }

    public string AcceptedByDisplayName { get; set; }

    /// <summary>The Microsoft Standard Contract checkbox was ticked (always true on a persisted row).</summary>
    public bool MicrosoftContractAccepted { get; set; }

    public string MicrosoftContractUrl { get; set; }

    public string MicrosoftContractVersion { get; set; }

    /// <summary>The publisher amendment checkbox was ticked (always true on a persisted row).</summary>
    public bool PublisherAmendmentAccepted { get; set; }

    public string PublisherAmendmentTitle { get; set; }

    public string PublisherAmendmentUrl { get; set; }

    public string PublisherAmendmentVersion { get; set; }

    public string IpAddress { get; set; }

    public string UserAgent { get; set; }

    /// <summary>Where the acceptance was captured, e.g. "Setup".</summary>
    public string Source { get; set; }
}
