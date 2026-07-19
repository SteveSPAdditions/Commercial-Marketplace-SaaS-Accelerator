using System;
using System.Text.Json;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Produces subscription-state-change signals onto the NotificationOutbox (drained + retried by
/// OutboxDrainService, delivered HMAC-signed to the Legeris signaling endpoint). Pull-nudge model:
/// the event names the tenant/subscription that changed and its new plan/status; RAU keys on the
/// tenant id, then re-pulls authoritative state from the Fulfillment API.
///
/// Runs from the webhook hot path, so it resolves its OWN scope + SaasKitContext via
/// IServiceScopeFactory (per the webhook DbContext-concurrency rule) rather than sharing the
/// request's scoped context. Best-effort: a failure here never breaks webhook processing - the
/// daily reconcile is the backstop. Idempotent on {eventType}|{sub:N}|{operationId:N}, so a webhook
/// redelivery (Microsoft + the buffer both retry) collapses to a single enqueued signal.
/// </summary>
public class SubscriptionSignalService : ISubscriptionSignalService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<SubscriptionSignalService> logger;

    public SubscriptionSignalService(IServiceScopeFactory scopeFactory, ILogger<SubscriptionSignalService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public void EnqueueSubscriptionSignal(Guid ampSubscriptionId, string eventType, Guid operationId)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var subscriptionsRepo = scope.ServiceProvider.GetRequiredService<ISubscriptionsRepository>();
            var outboxRepo = scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
            var context = scope.ServiceProvider.GetRequiredService<SaasKitContext>();

            // Read the CURRENT (post-update) state: this runs after the handler committed the change,
            // and on a fresh context, so it sees the new plan/status.
            var subscription = subscriptionsRepo.GetById(ampSubscriptionId);
            if (subscription == null)
            {
                this.logger.LogWarning("Subscription signal skipped: {SubscriptionId} not in DB.", ampSubscriptionId);
                return;
            }

            var idempotencyKey = $"{eventType}|{ampSubscriptionId:N}|{operationId:N}";
            if (outboxRepo.GetByIdempotencyKey(idempotencyKey) != null)
            {
                // Already enqueued for this operation (webhook redelivery). No-op.
                return;
            }

            var now = DateTime.UtcNow;
            var payload = new
            {
                eventType,
                saasSubscriptionId = ampSubscriptionId,
                assignedTenantId = subscription.PurchaserTenantId ?? Guid.Empty,
                planId = subscription.AmpplanId,
                subscriptionStatus = subscription.SubscriptionStatus,
                modifiedUtc = now,
                occurredBy = "Accelerator",
            };

            var entry = new NotificationOutbox
            {
                EventType = eventType,
                EventJson = JsonSerializer.Serialize(payload),
                AmpSubscriptionId = ampSubscriptionId,
                IdempotencyKey = idempotencyKey,
                CreatedUtc = now,
                NextAttemptUtc = now,
            };

            outboxRepo.Enqueue(entry);
            context.SaveChanges();

            this.logger.LogInformation(
                "Enqueued subscription signal {EventType} for {SubscriptionId} (tenant {TenantId}, plan {PlanId}).",
                eventType, ampSubscriptionId, payload.assignedTenantId, subscription.AmpplanId);
        }
        catch (Exception ex)
        {
            // Best-effort: never break webhook processing. The daily reconcile is the backstop.
            this.logger.LogError(ex, "Failed to enqueue subscription signal {EventType} for {SubscriptionId} (non-fatal).", eventType, ampSubscriptionId);
        }
    }
}
