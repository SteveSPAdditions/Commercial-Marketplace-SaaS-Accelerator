// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.DataAccess.Services;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.CustomerSite.HostedServices;

/// <summary>
/// Background drain loop for the NotificationOutbox table. Survives zip-deploy
/// or worker recycle: events written inside the same transaction as the
/// business-state row remain in the outbox until delivered.
/// </summary>
public class OutboxDrainService : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan[] BackoffSchedule = new[]
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(8),
        TimeSpan.FromHours(12),
        TimeSpan.FromHours(24),
    };

    private readonly IServiceScopeFactory scopeFactory;
    private readonly SaaSApiClientConfiguration config;
    private readonly ILogger<OutboxDrainService> logger;

    public OutboxDrainService(
        IServiceScopeFactory scopeFactory,
        SaaSApiClientConfiguration config,
        ILogger<OutboxDrainService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, this.config.OutboxDrainIntervalSeconds));
        this.logger.LogInformation("OutboxDrainService starting (interval: {Interval}s)", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.DrainOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "OutboxDrainService iteration failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        this.logger.LogInformation("OutboxDrainService stopping");
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var consentRepo = scope.ServiceProvider.GetRequiredService<ISubscriptionTenantConsentRepository>();

        var leased = repo.LeasePending(BatchSize, LeaseDuration);
        if (leased.Count == 0)
        {
            return;
        }

        this.logger.LogInformation("Outbox drain leased {Count} rows", leased.Count);

        foreach (var row in leased)
        {
            if (ct.IsCancellationRequested) break;
            await this.HandleOneAsync(row, repo, dispatcher, consentRepo, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleOneAsync(
        NotificationOutbox row,
        INotificationOutboxRepository repo,
        IOutboxDispatcher dispatcher,
        ISubscriptionTenantConsentRepository consentRepo,
        CancellationToken ct)
    {
        DispatchResult result;
        try
        {
            result = await dispatcher.TryDispatchAsync(row, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new DispatchResult
            {
                Outcome = DispatchOutcome.Transient,
                Error = $"Unhandled dispatcher exception: {ex.GetType().Name}: {ex.Message}",
            };
        }

        switch (result.Outcome)
        {
            case DispatchOutcome.Delivered:
                repo.MarkDelivered(row.Id, result.ResponseSnippet);
                this.OnDelivered(row, consentRepo);
                break;

            case DispatchOutcome.Permanent:
                repo.DeadLetter(row.Id, result.Error);
                this.logger.LogError(
                    "Outbox row {Id} dead-lettered (permanent): {Error}",
                    row.Id, result.Error);
                break;

            case DispatchOutcome.Transient:
            default:
                var attempt = row.Attempts; // current attempt count; LeasePending hasn't incremented yet
                if (attempt + 1 >= Math.Max(1, this.config.OutboxMaxAttempts))
                {
                    repo.DeadLetter(row.Id, $"Max attempts reached: {result.Error}");
                    this.logger.LogError(
                        "Outbox row {Id} dead-lettered after {Attempts} attempts: {Error}",
                        row.Id, attempt + 1, result.Error);
                }
                else
                {
                    var next = DateTime.UtcNow.Add(NextBackoff(attempt));
                    repo.MarkFailed(row.Id, result.Error, result.ResponseSnippet, next);
                    this.logger.LogWarning(
                        "Outbox row {Id} transient failure (attempt {Attempt}): {Error}; retry at {NextUtc}",
                        row.Id, attempt + 1, result.Error, next);
                }
                break;
        }
    }

    private void OnDelivered(NotificationOutbox row, ISubscriptionTenantConsentRepository consentRepo)
    {
        // Side effect: TenantRegionFanOut delivery updates the consent row so the
        // UI can flip to "propagated" on next status poll.
        if (string.Equals(row.EventType, "TenantRegionFanOut", StringComparison.Ordinal))
        {
            var consent = consentRepo.GetByAmpSubscriptionId(row.AmpSubscriptionId);
            if (consent != null && !consent.TenantRegionsFanOutCompleteUtc.HasValue)
            {
                consent.TenantRegionsFanOutCompleteUtc = DateTime.UtcNow;
                consentRepo.Save(consent);
            }
        }
    }

    private static TimeSpan NextBackoff(int attemptNumber)
    {
        var idx = Math.Min(attemptNumber, BackoffSchedule.Length - 1);
        return BackoffSchedule[idx];
    }
}
