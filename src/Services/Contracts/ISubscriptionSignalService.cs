using System;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Enqueues a subscription-state-change signal onto the NotificationOutbox for durable, retried,
/// HMAC-signed delivery to the RAU (Legeris) signaling endpoint. Pull-nudge model: the event tells
/// RAU which tenant/subscription changed; RAU re-pulls authoritative state. Best-effort and
/// idempotent - never throws into the webhook path.
/// </summary>
public interface ISubscriptionSignalService
{
    /// <summary>
    /// Enqueue a signal that the given subscription changed. Reads the current DB state
    /// (tenant / plan / status) for the event body.
    /// </summary>
    /// <param name="ampSubscriptionId">The AMP subscription id.</param>
    /// <param name="eventType">Event type: PlanChanged / Unsubscribed / Suspended / Reinstated.</param>
    /// <param name="operationId">The webhook OperationId - the idempotency discriminator so redeliveries collapse to one signal.</param>
    void EnqueueSubscriptionSignal(Guid ampSubscriptionId, string eventType, Guid operationId);
}
