// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Dispatches a single outbox row to its destination (currently the Legeris
/// EUSA signaling endpoint). Implementations are responsible for HMAC signing
/// and HTTP transport.
/// </summary>
public interface IOutboxDispatcher
{
    Task<DispatchResult> TryDispatchAsync(NotificationOutbox entry, CancellationToken ct);
}

/// <summary>Categorical outcome of a dispatch attempt.</summary>
public enum DispatchOutcome
{
    /// <summary>2xx or known idempotent-duplicate (409 with our event shape).</summary>
    Delivered,

    /// <summary>5xx, timeout, 408, 429 — retry with backoff.</summary>
    Transient,

    /// <summary>4xx other than 408/429 — dead-letter, retry will not help.</summary>
    Permanent,
}

public class DispatchResult
{
    public DispatchOutcome Outcome { get; set; }
    public string Error { get; set; }
    public string ResponseSnippet { get; set; }
}
