// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Services;

/// <summary>EF Core implementation of <see cref="ISubscriptionTermsAcceptanceRepository"/>.</summary>
public class SubscriptionTermsAcceptanceRepository : ISubscriptionTermsAcceptanceRepository
{
    private readonly SaasKitContext context;

    public SubscriptionTermsAcceptanceRepository(SaasKitContext context)
    {
        this.context = context;
    }

    public SubscriptionTermsAcceptance GetLatestByAmpSubscriptionId(Guid ampSubscriptionId)
    {
        return this.context.SubscriptionTermsAcceptance
            .Where(x => x.AmpSubscriptionId == ampSubscriptionId)
            .OrderByDescending(x => x.AcceptedUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
    }

    public IEnumerable<SubscriptionTermsAcceptance> ListByAmpSubscriptionId(Guid ampSubscriptionId)
    {
        return this.context.SubscriptionTermsAcceptance
            .Where(x => x.AmpSubscriptionId == ampSubscriptionId)
            .OrderByDescending(x => x.AcceptedUtc)
            .ThenByDescending(x => x.Id)
            .ToList();
    }

    public int Add(SubscriptionTermsAcceptance entity)
    {
        if (entity.AcceptedUtc == default)
        {
            entity.AcceptedUtc = DateTime.UtcNow;
        }

        this.context.SubscriptionTermsAcceptance.Add(entity);
        this.context.SaveChanges();
        return entity.Id;
    }
}
