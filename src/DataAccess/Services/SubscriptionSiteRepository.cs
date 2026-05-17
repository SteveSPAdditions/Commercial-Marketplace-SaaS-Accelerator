// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Services;

/// <summary>EF Core implementation of <see cref="ISubscriptionSiteRepository"/>.</summary>
public class SubscriptionSiteRepository : ISubscriptionSiteRepository
{
    private readonly SaasKitContext context;

    public SubscriptionSiteRepository(SaasKitContext context)
    {
        this.context = context;
    }

    public IEnumerable<SubscriptionSite> ListBySubscription(Guid ampSubscriptionId)
    {
        return this.context.SubscriptionSite
            .Where(x => x.AmpSubscriptionId == ampSubscriptionId)
            .OrderBy(x => x.Id)
            .ToList();
    }

    public SubscriptionSite Get(int id)
    {
        return this.context.SubscriptionSite.FirstOrDefault(x => x.Id == id);
    }

    public int Save(SubscriptionSite entity)
    {
        var now = DateTime.UtcNow;
        if (entity.Id == 0)
        {
            entity.CreatedUtc ??= now;
            entity.ModifiedUtc = now;
            this.context.SubscriptionSite.Add(entity);
        }
        else
        {
            entity.ModifiedUtc = now;
            this.context.SubscriptionSite.Update(entity);
        }

        this.context.SaveChanges();
        return entity.Id;
    }

    public void Remove(int id)
    {
        var row = this.context.SubscriptionSite.FirstOrDefault(x => x.Id == id);
        if (row != null)
        {
            this.context.SubscriptionSite.Remove(row);
            this.context.SaveChanges();
        }
    }
}
