// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Services;

public interface IPortalClient
{
    /// <summary>
    /// POSTs the buffered webhook body to the portal with an HMAC-signed body and the
    /// correlation headers the portal expects. Returns the raw response so the caller
    /// (Dispatcher) can classify and act on it.
    /// </summary>
    Task<HttpResponseMessage> PostWebhookAsync(string rawBody, string operationId, string activityId, CancellationToken ct);
}
