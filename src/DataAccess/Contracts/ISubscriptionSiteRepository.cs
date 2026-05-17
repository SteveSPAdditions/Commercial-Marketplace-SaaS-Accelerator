// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Contracts;

/// <summary>
/// Repository for SharePoint sites enrolled under a Marketplace subscription.
/// </summary>
public interface ISubscriptionSiteRepository
{
    /// <summary>All sites for a subscription, ordered by creation.</summary>
    IEnumerable<SubscriptionSite> ListBySubscription(Guid ampSubscriptionId);

    /// <summary>Get one site by row id.</summary>
    SubscriptionSite Get(int id);

    /// <summary>Insert or update.</summary>
    int Save(SubscriptionSite entity);

    /// <summary>Hard remove (soft-remove handled at status level by callers).</summary>
    void Remove(int id);
}
