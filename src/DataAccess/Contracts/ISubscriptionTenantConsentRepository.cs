// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Contracts;

/// <summary>
/// Repository for the per-subscription tenant-consent row that tracks region
/// selection and runtime-app admin consent.
/// </summary>
public interface ISubscriptionTenantConsentRepository
{
    /// <summary>Get the row for a Marketplace subscription, or null if it doesn't exist yet.</summary>
    SubscriptionTenantConsent GetByAmpSubscriptionId(Guid ampSubscriptionId);

    /// <summary>Get an existing row for a tenant (most recent), or null.</summary>
    SubscriptionTenantConsent GetByTenantId(Guid tenantId);

    /// <summary>Insert or update.</summary>
    int Save(SubscriptionTenantConsent entity);
}
