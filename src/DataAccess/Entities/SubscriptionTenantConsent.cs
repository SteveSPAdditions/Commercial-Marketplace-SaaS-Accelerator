// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.DataAccess.Entities;

public partial class SubscriptionTenantConsent
{
    public int Id { get; set; }

    public Guid AmpSubscriptionId { get; set; }

    public Guid TenantId { get; set; }

    public string AzureRegion { get; set; }

    public DateTime? AzureRegionSelectedUtc { get; set; }

    public string AzureRegionSelectedByUpn { get; set; }

    public DateTime? TenantRegionsFanOutCompleteUtc { get; set; }

    public string FanOutFailureRegions { get; set; }

    public DateTime? RuntimeAppConsentedUtc { get; set; }

    public string ConsentedByUpn { get; set; }

    public string ConsentedByObjectId { get; set; }

    public DateTime? CreatedUtc { get; set; }

    public DateTime? ModifiedUtc { get; set; }
}
