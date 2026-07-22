// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Text.Json;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.Services.StatusHandlers;

/// <summary>
/// Status handler to handle the subscriptions that are in PendingActivation status.
/// </summary>
/// <seealso cref="Microsoft.Marketplace.SaasKit.Provisioning.Webjob.StatusHandlers.AbstractSubscriptionStatusHandler" />
public class PendingActivationStatusHandler : AbstractSubscriptionStatusHandler
{
    /// <summary>
    /// The fulfillment apiclient.
    /// </summary>
    private readonly IFulfillmentApiService fulfillmentApiService;

    /// <summary>
    /// The subscription log repository.
    /// </summary>
    private readonly ISubscriptionLogRepository subscriptionLogRepository;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger<PendingActivationStatusHandler> logger;

    /// <summary>
    /// Enqueues subscription-state-change signals to RAU via the outbox. Optional/nullable: callers
    /// that don't wire it up simply emit no signal (the daily reconcile remains the backstop).
    /// </summary>
    private readonly ISubscriptionSignalService subscriptionSignalService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PendingActivationStatusHandler"/> class.
    /// </summary>
    /// <param name="fulfillApiService">The fulfill API client.</param>
    /// <param name="subscriptionsRepository">The subscriptions repository.</param>
    /// <param name="subscriptionLogRepository">The subscription log repository.</param>
    /// <param name="plansRepository">The plans repository.</param>
    /// <param name="usersRepository">The users repository.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="subscriptionSignalService">Enqueues the RAU "Activated" signal on successful activation.</param>
    public PendingActivationStatusHandler(
        IFulfillmentApiService fulfillApiService,
        ISubscriptionsRepository subscriptionsRepository,
        ISubscriptionLogRepository subscriptionLogRepository,
        IPlansRepository plansRepository,
        IUsersRepository usersRepository,
        ILogger<PendingActivationStatusHandler> logger,
        ISubscriptionSignalService subscriptionSignalService = null)
        : base(subscriptionsRepository, plansRepository, usersRepository)
    {
        this.fulfillmentApiService = fulfillApiService;
        this.subscriptionLogRepository = subscriptionLogRepository;
        this.logger = logger;
        this.subscriptionSignalService = subscriptionSignalService;
    }

    /// <summary>
    /// Processes the specified subscription identifier.
    /// </summary>
    /// <param name="subscriptionID">The subscription identifier.</param>
    public override void Process(Guid subscriptionID)
    {
        this.logger?.LogInformation("PendingActivationStatusHandler {0}", subscriptionID);
        var subscription = this.GetSubscriptionById(subscriptionID);
        this.logger?.LogInformation("Result subscription : {0}", JsonSerializer.Serialize(subscription.AmpplanId));
        this.logger?.LogInformation("Get User");
        var userdeatils = this.GetUserById(subscription.UserId);
        string oldstatus = subscription.SubscriptionStatus;

        if (subscription.SubscriptionStatus == SubscriptionStatusEnumExtension.PendingActivation.ToString())
        {
            try
            {
                this.logger?.LogInformation("Get attributelsit");

                var subscriptionData = this.fulfillmentApiService.ActivateSubscriptionAsync(subscriptionID, subscription.AmpplanId).ConfigureAwait(false).GetAwaiter().GetResult();

                this.logger?.LogInformation("UpdateWebJobSubscriptionStatus");

                this.subscriptionsRepository.UpdateStatusForSubscription(subscriptionID, SubscriptionStatusEnumExtension.Subscribed.ToString(), true);

                SubscriptionAuditLogs auditLog = new SubscriptionAuditLogs()
                {
                    Attribute = SubscriptionLogAttributes.Status.ToString(),
                    SubscriptionId = subscription.Id,
                    NewValue = SubscriptionStatusEnumExtension.Subscribed.ToString(),
                    OldValue = oldstatus,
                    CreateBy = userdeatils.UserId,
                    CreateDate = DateTime.Now,
                };
                this.subscriptionLogRepository.Save(auditLog);

                this.subscriptionLogRepository.LogStatusDuringProvisioning(subscriptionID, "Activated", SubscriptionStatusEnumExtension.Subscribed.ToString());

                // Signal RAU that the subscription is now active. This is what flips RAU's cached
                // MarketplaceSubscriptionStatus back to Subscribed on a resubscribe (the only path that
                // does so once MarketplaceStatusSource="Cached"; inert but harmless under "Live"). No
                // Marketplace operation id here -- pass Guid.Empty; the signal service keys such
                // occurrences uniquely (delivered outbox rows are retained, so a constant key would
                // suppress future signals). Best-effort; the signal service never throws.
                this.subscriptionSignalService?.EnqueueSubscriptionSignal(subscriptionID, "Activated", Guid.Empty);
            }
            catch (Exception ex)
            {
                string errorDescriptin = string.Format("Exception: {0} :: Innser Exception:{1}", ex.Message, ex.InnerException);
                this.subscriptionLogRepository.LogStatusDuringProvisioning(subscriptionID, errorDescriptin, SubscriptionStatusEnumExtension.ActivationFailed.ToString());
                this.logger?.LogInformation(errorDescriptin);

                this.subscriptionsRepository.UpdateStatusForSubscription(subscriptionID, SubscriptionStatusEnumExtension.ActivationFailed.ToString(), false);

                // Set the status as ActivationFailed.
                SubscriptionAuditLogs auditLog = new SubscriptionAuditLogs()
                {
                    Attribute = SubscriptionLogAttributes.Status.ToString(),
                    SubscriptionId = subscription.Id,
                    NewValue = SubscriptionStatusEnumExtension.ActivationFailed.ToString(),
                    OldValue = subscription.SubscriptionStatus,
                    CreateBy = userdeatils.UserId,
                    CreateDate = DateTime.Now,
                };
                this.subscriptionLogRepository.Save(auditLog);
            }
        }
    }
}