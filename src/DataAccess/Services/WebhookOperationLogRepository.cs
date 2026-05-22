// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Marketplace.SaaS.Accelerator.DataAccess.Services;

/// <summary>
/// EF Core implementation of <see cref="IWebhookOperationLogRepository"/>. Creates a fresh
/// <see cref="SaasKitContext"/> per call via <see cref="IServiceScopeFactory"/> so the
/// idempotency check/save is isolated from any other in-flight DbContext operations on the
/// request's shared scoped context (e.g. fire-and-forget application-log writes from email
/// or notification handlers that can race with this repo otherwise).
/// </summary>
public class WebhookOperationLogRepository : IWebhookOperationLogRepository
{
    private readonly IServiceScopeFactory scopeFactory;

    public WebhookOperationLogRepository(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    public WebhookOperationLog Get(Guid operationId)
    {
        using var scope = this.scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SaasKitContext>();
        return context.WebhookOperationLog
            .FirstOrDefault(x => x.OperationId == operationId);
    }

    public void Save(WebhookOperationLog entry)
    {
        if (entry == null || entry.OperationId == Guid.Empty)
        {
            return;
        }

        using var scope = this.scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SaasKitContext>();

        var existing = context.WebhookOperationLog
            .FirstOrDefault(x => x.OperationId == entry.OperationId);
        if (existing != null)
        {
            return;
        }

        if (entry.ReceivedUtc == default)
        {
            entry.ReceivedUtc = DateTime.UtcNow;
        }

        context.WebhookOperationLog.Add(entry);
        context.SaveChanges();
    }
}
