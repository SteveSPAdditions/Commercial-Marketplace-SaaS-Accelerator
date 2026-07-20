// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Services;

/// <summary>EF Core implementation of <see cref="INotificationOutboxRepository"/>.</summary>
public class NotificationOutboxRepository : INotificationOutboxRepository
{
    private readonly SaasKitContext context;

    public NotificationOutboxRepository(SaasKitContext context)
    {
        this.context = context;
    }

    public int Enqueue(NotificationOutbox entry)
    {
        if (entry.CreatedUtc == default)
        {
            entry.CreatedUtc = DateTime.UtcNow;
        }
        if (entry.NextAttemptUtc == default)
        {
            entry.NextAttemptUtc = entry.CreatedUtc;
        }

        this.context.NotificationOutbox.Add(entry);
        // No SaveChanges() here — caller owns the transaction.
        return entry.Id;
    }

    public IReadOnlyList<NotificationOutbox> LeasePending(int limit, TimeSpan leaseDuration)
    {
        var now = DateTime.UtcNow;
        var leaseUntil = now.Add(leaseDuration);

        // Atomically: pick rows that are due and either not leased or whose lease has expired,
        // mark them leased, save, return. Concurrent drainers will lose the race on
        // SaveChanges via the lease window.
        var candidates = this.context.NotificationOutbox
            .Where(x => !x.DeadLettered
                        && x.DeliveredUtc == null
                        && x.NextAttemptUtc <= now
                        && (x.LeasedUntilUtc == null || x.LeasedUntilUtc < now))
            .OrderBy(x => x.NextAttemptUtc)
            .Take(limit)
            .ToList();

        foreach (var row in candidates)
        {
            row.LeasedUntilUtc = leaseUntil;
        }

        this.context.SaveChanges();
        return candidates;
    }

    public int MarkDelivered(int id, string responseSnippet)
    {
        var row = this.context.NotificationOutbox.FirstOrDefault(x => x.Id == id);
        if (row == null) return 0;
        row.DeliveredUtc = DateTime.UtcNow;
        row.LastResponseSnippet = Trim(responseSnippet, 512);
        row.LeasedUntilUtc = null;
        // Clear any error from earlier failed attempts so a delivered row reads cleanly
        // (delivery is authoritative via DeliveredUtc; Attempts still shows it took retries).
        row.LastError = null;
        return this.context.SaveChanges();
    }

    public int MarkFailed(int id, string error, string responseSnippet, DateTime nextAttemptUtc)
    {
        var row = this.context.NotificationOutbox.FirstOrDefault(x => x.Id == id);
        if (row == null) return 0;
        row.Attempts += 1;
        row.LastError = Trim(error, 2000);
        row.LastResponseSnippet = Trim(responseSnippet, 512);
        row.NextAttemptUtc = nextAttemptUtc;
        row.LeasedUntilUtc = null;
        return this.context.SaveChanges();
    }

    public int DeadLetter(int id, string finalError)
    {
        var row = this.context.NotificationOutbox.FirstOrDefault(x => x.Id == id);
        if (row == null) return 0;
        row.DeadLettered = true;
        row.LastError = Trim(finalError, 2000);
        row.LeasedUntilUtc = null;
        return this.context.SaveChanges();
    }

    public NotificationOutbox GetByIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrEmpty(idempotencyKey)) return null;
        return this.context.NotificationOutbox
            .FirstOrDefault(x => x.IdempotencyKey == idempotencyKey);
    }

    public IEnumerable<NotificationOutbox> ListDeadLettered()
    {
        return this.context.NotificationOutbox
            .Where(x => x.DeadLettered)
            .OrderByDescending(x => x.Id)
            .ToList();
    }

    public int Retry(int id)
    {
        var row = this.context.NotificationOutbox.FirstOrDefault(x => x.Id == id);
        if (row == null) return 0;
        row.DeadLettered = false;
        row.Attempts = 0;
        row.NextAttemptUtc = DateTime.UtcNow;
        row.LeasedUntilUtc = null;
        return this.context.SaveChanges();
    }

    private static string Trim(string s, int max)
    {
        if (s == null) return null;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
