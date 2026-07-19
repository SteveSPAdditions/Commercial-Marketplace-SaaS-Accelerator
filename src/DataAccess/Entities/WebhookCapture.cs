using System;

namespace Marketplace.SaaS.Accelerator.DataAccess.Entities;

/// <summary>
/// Archives every inbound Marketplace webhook the portal receives, so real payloads can be
/// replayed for repetitive testing without creating new marketplace subscriptions. The payload
/// is stored re-serialized in wire format (camelCase + string enums, honoring the DTO's
/// [JsonPropertyName("Id")]) so it binds back to an identical WebhookPayload on replay. Byte
/// fidelity is not required: replay re-signs with a fresh OperationId anyway.
/// </summary>
public partial class WebhookCapture
{
    /// <summary>Surrogate PK (identity).</summary>
    public int Id { get; set; }

    /// <summary>UTC timestamp the payload was captured, on arrival.</summary>
    public DateTime CapturedUtc { get; set; }

    /// <summary>Webhook action (Unsubscribe / ChangePlan / etc.) for listing/diagnostics.</summary>
    public string Action { get; set; }

    /// <summary>Subscription id from the payload - indexed.</summary>
    public Guid? SubscriptionId { get; set; }

    /// <summary>Operation id (json `Id`) as received.</summary>
    public Guid? OperationId { get; set; }

    /// <summary>How the call arrived: Buffer (via the WebhookBuffer function) or Direct.</summary>
    public string Source { get; set; }

    /// <summary>Outcome hint recorded at capture time (Received). Processing outcome is in ApplicationLog / WebhookOperationLog.</summary>
    public string ResultStatus { get; set; }

    /// <summary>Replayable JSON body (wire format).</summary>
    public string PayloadJson { get; set; }
}
