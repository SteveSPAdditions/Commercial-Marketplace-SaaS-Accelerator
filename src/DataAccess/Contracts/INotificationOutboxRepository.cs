// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Contracts;

/// <summary>
/// Persistent outbox for outbound events that survive zip-deploy/worker recycles.
/// Caller is expected to enqueue inside the same DbContext transaction as the
/// business-state write that the event reflects.
/// </summary>
public interface INotificationOutboxRepository
{
    /// <summary>
    /// Enqueue an outbox entry. Caller manages the surrounding transaction so
    /// the event row commits with the business-state row.
    /// </summary>
    int Enqueue(NotificationOutbox entry);

    /// <summary>
    /// Lease a batch of due rows (NextAttemptUtc &lt;= now, not delivered, not dead-lettered,
    /// not already leased by another worker). Returns the leased rows; LeasedUntilUtc set.
    /// </summary>
    IReadOnlyList<NotificationOutbox> LeasePending(int limit, TimeSpan leaseDuration);

    /// <summary>Mark a row as delivered.</summary>
    int MarkDelivered(int id, string responseSnippet);

    /// <summary>Bump attempts + schedule next try.</summary>
    int MarkFailed(int id, string error, string responseSnippet, DateTime nextAttemptUtc);

    /// <summary>Dead-letter; will not be retried by the drain loop.</summary>
    int DeadLetter(int id, string finalError);

    /// <summary>Look up by idempotency key (used to detect duplicates pre-enqueue).</summary>
    NotificationOutbox GetByIdempotencyKey(string idempotencyKey);

    /// <summary>List dead-lettered rows for admin diagnostics.</summary>
    IEnumerable<NotificationOutbox> ListDeadLettered();

    /// <summary>Reset a dead-lettered row for manual retry.</summary>
    int Retry(int id);
}
