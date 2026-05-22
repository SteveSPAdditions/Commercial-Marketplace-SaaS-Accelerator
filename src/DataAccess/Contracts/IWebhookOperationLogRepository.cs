// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Contracts;

/// <summary>
/// Tracks Marketplace webhook OperationIds the portal has processed. Used to make the
/// webhook endpoint idempotent on retries (Microsoft and/or the WebhookBuffer may
/// deliver the same OperationId more than once).
/// </summary>
public interface IWebhookOperationLogRepository
{
    /// <summary>Look up an existing entry. Null if this OperationId has not been processed.</summary>
    WebhookOperationLog Get(Guid operationId);

    /// <summary>Record a newly-processed OperationId. Idempotent — second call with the same id is a no-op.</summary>
    void Save(WebhookOperationLog entry);
}
