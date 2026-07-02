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
        var sites = this.siteRepo.ListBySubscription(ampSubscriptionId).ToList();
        return Build(ampSubscriptionId, consent, sites);
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
        IReadOnlyCollection<DataAccess.Entities.SubscriptionSite> sites)
    {
        var regionSelected = consent?.AzureRegion != null;
        var regionFanOut = consent?.TenantRegionsFanOutCompleteUtc.HasValue == true;
        var consented = consent?.RuntimeAppConsentedUtc.HasValue == true;
        var teamsActivityConsented = consent?.TeamsActivityAppConsentedUtc.HasValue == true;

        var hasSites = sites.Count > 0;
        // The sites step counts as complete only when every enrolled site has been granted.
        // A site still Pending, or one whose grant Failed, must leave the step -- and
        // therefore the whole Setup -- incomplete.
        var sitesComplete = hasSites
            && sites.All(s => string.Equals(s.Status, "Granted", StringComparison.OrdinalIgnoreCase));

        // Step 1 (subscription active) is implicit when this is called.
        var completed = 1
            + (regionSelected && regionFanOut ? 1 : 0)
            + (consented ? 1 : 0)
            + (sitesComplete ? 1 : 0)
            + (teamsActivityConsented ? 1 : 0);

        return new SetupStatusSummary
        {
            AmpSubscriptionId = ampSubscriptionId,
            RegionSelected = regionSelected,
            RegionFanOutComplete = regionFanOut,
            TenantConsented = consented,
            TeamsActivityConsented = teamsActivityConsented,
            HasSites = hasSites,
            CompletedSteps = completed,
            SetupUrl = $"/Setup/{ampSubscriptionId}",
        };
    }
}
