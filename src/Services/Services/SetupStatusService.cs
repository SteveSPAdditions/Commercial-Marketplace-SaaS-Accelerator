// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;

namespace Marketplace.SaaS.Accelerator.Services.Services;

public class SetupStatusService : ISetupStatusService
{
    private readonly ISubscriptionTenantConsentRepository consentRepo;
    private readonly ISubscriptionSiteRepository siteRepo;

    public SetupStatusService(
        ISubscriptionTenantConsentRepository consentRepo,
        ISubscriptionSiteRepository siteRepo)
    {
        this.consentRepo = consentRepo;
        this.siteRepo = siteRepo;
    }

    public SetupStatusSummary GetStatus(Guid ampSubscriptionId)
    {
        var consent = this.consentRepo.GetByAmpSubscriptionId(ampSubscriptionId);
        var hasSites = this.siteRepo.ListBySubscription(ampSubscriptionId).Any();
        return Build(ampSubscriptionId, consent, hasSites);
    }

    public IDictionary<Guid, SetupStatusSummary> GetStatuses(IEnumerable<Guid> ampSubscriptionIds)
    {
        var result = new Dictionary<Guid, SetupStatusSummary>();
        if (ampSubscriptionIds == null)
        {
            return result;
        }

        foreach (var id in ampSubscriptionIds.Distinct())
        {
            result[id] = this.GetStatus(id);
        }
        return result;
    }

    private static SetupStatusSummary Build(
        Guid ampSubscriptionId,
        DataAccess.Entities.SubscriptionTenantConsent consent,
        bool hasSites)
    {
        var regionSelected = consent?.AzureRegion != null;
        var regionFanOut = consent?.TenantRegionsFanOutCompleteUtc.HasValue == true;
        var consented = consent?.RuntimeAppConsentedUtc.HasValue == true;

        // Step 1 (subscription active) is implicit when this is called.
        var completed = 1
            + (regionSelected && regionFanOut ? 1 : 0)
            + (consented ? 1 : 0)
            + (hasSites ? 1 : 0);

        return new SetupStatusSummary
        {
            AmpSubscriptionId = ampSubscriptionId,
            RegionSelected = regionSelected,
            RegionFanOutComplete = regionFanOut,
            TenantConsented = consented,
            HasSites = hasSites,
            CompletedSteps = completed,
            SetupUrl = $"/Setup/{ampSubscriptionId}",
        };
    }
}
