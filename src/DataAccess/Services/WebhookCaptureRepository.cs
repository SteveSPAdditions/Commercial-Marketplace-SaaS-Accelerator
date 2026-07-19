using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Marketplace.SaaS.Accelerator.DataAccess.Services;

/// <summary>
/// Own-context repository for <see cref="WebhookCapture"/>. Like WebhookOperationLogRepository,
/// it resolves a fresh SaasKitContext per call via IServiceScopeFactory so writes from the
/// webhook hot path never race the request's shared scoped context (which carries fire-and-forget
/// application-log writes).
/// </summary>
public class WebhookCaptureRepository : IWebhookCaptureRepository
{
    private readonly IServiceScopeFactory scopeFactory;

    public WebhookCaptureRepository(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    public void Save(WebhookCapture entry)
    {
        if (entry == null)
        {
            return;
        }

        using var scope = this.scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SaasKitContext>();

        if (entry.CapturedUtc == default)
        {
            entry.CapturedUtc = DateTime.UtcNow;
        }

        context.WebhookCapture.Add(entry);
        context.SaveChanges();
    }

    public List<WebhookCapture> ListRecent(int max = 100)
    {
        using var scope = this.scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SaasKitContext>();
        return context.WebhookCapture
            .OrderByDescending(x => x.Id)
            .Take(max)
            .ToList();
    }

    public WebhookCapture Get(int id)
    {
        using var scope = this.scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SaasKitContext>();
        return context.WebhookCapture.FirstOrDefault(x => x.Id == id);
    }
}
