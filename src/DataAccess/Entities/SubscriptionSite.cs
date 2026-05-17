// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.DataAccess.Entities;

public partial class SubscriptionSite
{
    public int Id { get; set; }

    public Guid AmpSubscriptionId { get; set; }

    public string SharePointSiteUrl { get; set; }

    public string GraphSiteId { get; set; }

    public string Status { get; set; }

    public string CurrentRole { get; set; }

    public string PermissionId { get; set; }

    public DateTime? GrantedUtc { get; set; }

    public string GrantedByUpn { get; set; }

    public DateTime? DowngradedUtc { get; set; }

    public string FailureReason { get; set; }

    public DateTime? CreatedUtc { get; set; }

    public DateTime? ModifiedUtc { get; set; }
}
