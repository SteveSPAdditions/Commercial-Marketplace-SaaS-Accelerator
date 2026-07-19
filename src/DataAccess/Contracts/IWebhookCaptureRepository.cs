using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Contracts;

/// <summary>
/// Archive of inbound Marketplace webhooks, for replay-based testing.
/// </summary>
public interface IWebhookCaptureRepository
{
    /// <summary>Archive an inbound webhook. Best-effort; callers in the webhook path swallow failures.</summary>
    void Save(WebhookCapture entry);

    /// <summary>Most recent captures, newest first (capped at <paramref name="max"/>).</summary>
    List<WebhookCapture> ListRecent(int max = 100);

    /// <summary>Single capture by id, or null.</summary>
    WebhookCapture Get(int id);
}
