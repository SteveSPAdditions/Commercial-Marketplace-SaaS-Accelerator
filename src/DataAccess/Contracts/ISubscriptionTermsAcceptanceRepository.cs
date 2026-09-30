// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Contracts;

/// <summary>
/// Append-only repository for the per-subscription terms-acceptance record.
/// </summary>
public interface ISubscriptionTermsAcceptanceRepository
{
    /// <summary>Most recent acceptance for a Marketplace subscription, or null if none has been recorded.</summary>
    SubscriptionTermsAcceptance GetLatestByAmpSubscriptionId(Guid ampSubscriptionId);

    /// <summary>Every acceptance recorded for a Marketplace subscription, newest first.</summary>
    IEnumerable<SubscriptionTermsAcceptance> ListByAmpSubscriptionId(Guid ampSubscriptionId);

    /// <summary>Insert a new acceptance row. Rows are never updated.</summary>
    int Add(SubscriptionTermsAcceptance entity);
}
