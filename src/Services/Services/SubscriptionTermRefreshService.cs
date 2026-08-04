// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Refreshes the locally-cached subscription term (Term / StartDate / EndDate) from a live
/// Fulfillment API pull. The marketplace webhook payload carries NO term data, so any event
/// that moves the term -- activation, ChangePlan (term unit can change), and above all Renew
/// (a new term starts, TermStart moves forward) -- must re-pull it or the local copy silently
/// goes stale. The regional tenantregions rows derive MarketplaceTermStartUtc from the
/// subscription signal, which reads these columns, so a stale StartDate silently moves the
/// metered-billing emission window.
///
/// Best-effort by design: the caller's own state change has already committed, and failing the
/// whole webhook for a term-pull hiccup would make Microsoft retry an already-applied action.
/// A missed refresh self-corrects on the next lifecycle event, and the daily reconcile plus
/// snapshot staleness tolerance are the backstops.
/// </summary>
public class SubscriptionTermRefreshService
{
    private readonly IFulfillmentApiService fulfillmentApiService;
    private readonly ISubscriptionsRepository subscriptionsRepository;
    private readonly ILogger logger;

    public SubscriptionTermRefreshService(
        IFulfillmentApiService fulfillmentApiService,
        ISubscriptionsRepository subscriptionsRepository,
        ILogger logger = null)
    {
        this.fulfillmentApiService = fulfillmentApiService;
        this.subscriptionsRepository = subscriptionsRepository;
        this.logger = logger;
    }

    /// <summary>
    /// Pulls the subscription from the Fulfillment API and persists its term columns.
    /// Returns true when the term was refreshed, false when skipped or failed (non-fatal).
    /// </summary>
    public async Task<bool> RefreshTermAsync(Guid subscriptionId)
    {
        try
        {
            var live = await this.fulfillmentApiService.GetSubscriptionByIdAsync(subscriptionId).ConfigureAwait(false);
            if (live?.Term == null || live.Id == Guid.Empty)
            {
                this.logger?.LogWarning(
                    "Term refresh skipped for {SubscriptionId}: Fulfillment API returned no usable subscription.",
                    subscriptionId);
                return false;
            }

            this.subscriptionsRepository.UpdateTermForSubscription(
                subscriptionId,
                live.Term.TermUnit.ToString(),
                live.Term.StartDate.ToUniversalTime().DateTime,
                live.Term.EndDate.ToUniversalTime().DateTime);

            this.logger?.LogInformation(
                "Term refreshed for {SubscriptionId}: {TermUnit} {StartDate:O} -> {EndDate:O}.",
                subscriptionId, live.Term.TermUnit, live.Term.StartDate, live.Term.EndDate);
            return true;
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(
                ex,
                "Term refresh failed for {SubscriptionId} (non-fatal; next lifecycle event or reconcile corrects it).",
                subscriptionId);
            return false;
        }
    }
}
