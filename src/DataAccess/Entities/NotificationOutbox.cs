// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.DataAccess.Entities;

public partial class NotificationOutbox
{
    public int Id { get; set; }

    public string EventType { get; set; }

    public string EventJson { get; set; }

    public Guid AmpSubscriptionId { get; set; }

    public string IdempotencyKey { get; set; }

    public int Attempts { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime NextAttemptUtc { get; set; }

    public DateTime? DeliveredUtc { get; set; }

    public string LastError { get; set; }

    public string LastResponseSnippet { get; set; }

    public bool DeadLettered { get; set; }

    public DateTime? LeasedUntilUtc { get; set; }
}
