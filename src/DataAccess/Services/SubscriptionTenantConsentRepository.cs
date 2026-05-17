// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Services;

/// <summary>EF Core implementation of <see cref="ISubscriptionTenantConsentRepository"/>.</summary>
public class SubscriptionTenantConsentRepository : ISubscriptionTenantConsentRepository
{
    private readonly SaasKitContext context;

    public SubscriptionTenantConsentRepository(SaasKitContext context)
    {
        this.context = context;
    }

    public SubscriptionTenantConsent GetByAmpSubscriptionId(Guid ampSubscriptionId)
    {
        return this.context.SubscriptionTenantConsent
            .FirstOrDefault(x => x.AmpSubscriptionId == ampSubscriptionId);
    }

    public SubscriptionTenantConsent GetByTenantId(Guid tenantId)
    {
        return this.context.SubscriptionTenantConsent
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.Id)
            .FirstOrDefault();
    }

    public int Save(SubscriptionTenantConsent entity)
    {
        var now = DateTime.UtcNow;
        if (entity.Id == 0)
        {
            entity.CreatedUtc ??= now;
            entity.ModifiedUtc = now;
            this.context.SubscriptionTenantConsent.Add(entity);
        }
        else
        {
            entity.ModifiedUtc = now;
            this.context.SubscriptionTenantConsent.Update(entity);
        }

        this.context.SaveChanges();
        return entity.Id;
    }
}
