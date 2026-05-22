// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.DataAccess.Entities;

/// <summary>
/// Records each Marketplace webhook OperationId the portal has processed. Used by the
/// AzureWebhookController to short-circuit duplicate deliveries (the buffer adds
/// at-least-once semantics on top of Microsoft's own retry policy).
/// </summary>
public partial class WebhookOperationLog
{
    /// <summary>Operation id from the Marketplace payload (json `id`). PK.</summary>
    public Guid OperationId { get; set; }

    /// <summary>UTC timestamp the portal first processed this operation.</summary>
    public DateTime ReceivedUtc { get; set; }

    /// <summary>Webhook action (Unsubscribe / ChangePlan / etc.) for diagnostics.</summary>
    public string Action { get; set; }

    /// <summary>Subscription id for diagnostics — indexed.</summary>
    public Guid? SubscriptionId { get; set; }

    /// <summary>Result of processing: Processed / Rejected / Error.</summary>
    public string ResultStatus { get; set; }
}
