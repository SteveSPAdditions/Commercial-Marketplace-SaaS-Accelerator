// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Functions;

/// <summary>
/// Service Bus-triggered Function that delivers buffered webhook messages to the portal.
/// Classifies the portal response into Delivered / Transient / Permanent and resolves
/// the SB message accordingly. Inline single retry absorbs micro-blips; further retries
/// are delegated to Service Bus redelivery (MaxDeliveryCount = 10) so each attempt gets
/// a fresh lock.
/// </summary>
public class WebhookDispatcher
{
    private readonly IPortalClient portalClient;
    private readonly ILogger<WebhookDispatcher> logger;

    public WebhookDispatcher(IPortalClient portalClient, ILogger<WebhookDispatcher> logger)
    {
        this.portalClient = portalClient;
        this.logger = logger;
    }

    [Function("WebhookDispatcher")]
    public async Task Run(
        [ServiceBusTrigger("%BufferOptions:QueueName%", Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken ct)
    {
        var operationId = GetProperty(message, "OperationId") ?? message.MessageId ?? string.Empty;
        var activityId = GetProperty(message, "MsActivityId") ?? message.CorrelationId ?? string.Empty;
        var action = GetProperty(message, "Action") ?? string.Empty;
        var rawBody = message.Body?.ToString() ?? string.Empty;

        this.logger.LogInformation(
            "Dispatching webhook OperationId={OperationId} Action={Action} DeliveryCount={DeliveryCount}",
            operationId, action, message.DeliveryCount);

        var classification = await this.TryDeliverWithInlineRetryAsync(rawBody, operationId, activityId, ct).ConfigureAwait(false);

        switch (classification.Outcome)
        {
            case DeliveryOutcome.Delivered:
                await messageActions.CompleteMessageAsync(message, ct).ConfigureAwait(false);
                this.logger.LogInformation(
                    "Webhook delivered OperationId={OperationId} Status={Status}",
                    operationId, classification.StatusCode);
                break;

            case DeliveryOutcome.Transient:
                this.logger.LogWarning(
                    "Webhook transient failure OperationId={OperationId} Status={Status} Reason={Reason}; abandoning for redelivery",
                    operationId, classification.StatusCode, classification.Reason);
                await messageActions.AbandonMessageAsync(message, null, ct).ConfigureAwait(false);
                break;

            case DeliveryOutcome.Permanent:
                this.logger.LogError(
                    "Webhook permanent failure OperationId={OperationId} Status={Status} Reason={Reason}; dead-lettering",
                    operationId, classification.StatusCode, classification.Reason);
                await messageActions.DeadLetterMessageAsync(
                    message,
                    propertiesToModify: null,
                    deadLetterReason: classification.Reason ?? "PortalRejected",
                    deadLetterErrorDescription: classification.Snippet,
                    ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task<Classification> TryDeliverWithInlineRetryAsync(string body, string operationId, string activityId, CancellationToken ct)
    {
        var first = await this.TryDeliverOnceAsync(body, operationId, activityId, ct).ConfigureAwait(false);
        if (first.Outcome != DeliveryOutcome.Transient)
        {
            return first;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return first;
        }

        return await this.TryDeliverOnceAsync(body, operationId, activityId, ct).ConfigureAwait(false);
    }

    private async Task<Classification> TryDeliverOnceAsync(string body, string operationId, string activityId, CancellationToken ct)
    {
        try
        {
            using var response = await this.portalClient.PostWebhookAsync(body, operationId, activityId, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var snippet = await ReadSnippetAsync(response, ct).ConfigureAwait(false);

            if (status >= 200 && status < 300)
            {
                return new Classification(DeliveryOutcome.Delivered, status, null, snippet);
            }

            if (status == (int)HttpStatusCode.RequestTimeout
                || status == 429
                || (status >= 500 && status < 600))
            {
                return new Classification(DeliveryOutcome.Transient, status, $"HTTP {status}", snippet);
            }

            if (status == (int)HttpStatusCode.Unauthorized || status == (int)HttpStatusCode.Forbidden)
            {
                return new Classification(DeliveryOutcome.Permanent, status, "PortalAuthFailed", snippet);
            }

            return new Classification(DeliveryOutcome.Permanent, status, "PortalRejected", snippet);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return new Classification(DeliveryOutcome.Transient, null, "Timeout", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new Classification(DeliveryOutcome.Transient, null, "HttpRequestException", ex.Message);
        }
    }

    private static async Task<string?> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(content)) return null;
            return content.Length <= 512 ? content : content.Substring(0, 512);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetProperty(ServiceBusReceivedMessage message, string key)
    {
        return message.ApplicationProperties.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private enum DeliveryOutcome
    {
        Delivered,
        Transient,
        Permanent,
    }

    private readonly record struct Classification(DeliveryOutcome Outcome, int? StatusCode, string? Reason, string? Snippet);
}
