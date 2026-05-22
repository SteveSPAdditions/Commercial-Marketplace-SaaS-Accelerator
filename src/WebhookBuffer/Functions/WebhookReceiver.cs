// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Functions;

/// <summary>
/// HTTP-triggered Function that absorbs Microsoft Marketplace webhook POSTs. Validates
/// the inbound JWT, enqueues the raw body to Service Bus, and returns 202 within Microsoft's
/// ~10-second deadline (target p99 &lt; 500 ms). All downstream processing is handled
/// asynchronously by <see cref="WebhookDispatcher"/>.
/// </summary>
public class WebhookReceiver
{
    private readonly IJwtValidator jwtValidator;
    private readonly ServiceBusSender sender;
    private readonly ILogger<WebhookReceiver> logger;

    public WebhookReceiver(IJwtValidator jwtValidator, ServiceBusSender sender, ILogger<WebhookReceiver> logger)
    {
        this.jwtValidator = jwtValidator;
        this.sender = sender;
        this.logger = logger;
    }

    [Function("WebhookReceiver")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "marketplace-webhook")] HttpRequest req)
    {
        string rawBody;
        using (var reader = new StreamReader(req.Body))
        {
            rawBody = await reader.ReadToEndAsync(req.HttpContext.RequestAborted).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            this.logger.LogWarning("Empty webhook body received");
            return new BadRequestObjectResult(new { error = "empty body" });
        }

        WebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WebhookEnvelope>(rawBody);
        }
        catch (JsonException ex)
        {
            this.logger.LogWarning(ex, "Webhook body is not valid JSON");
            return new BadRequestObjectResult(new { error = "invalid JSON" });
        }

        if (envelope == null || string.IsNullOrWhiteSpace(envelope.OperationId))
        {
            this.logger.LogWarning("Webhook body missing required 'id' (operation id) field");
            return new BadRequestObjectResult(new { error = "missing operation id" });
        }

        var authHeader = req.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            this.logger.LogWarning("Webhook missing Bearer token; OperationId={OperationId}", envelope.OperationId);
            return new UnauthorizedResult();
        }

        var token = authHeader.Substring("Bearer ".Length).Trim();
        bool jwtValid;
        try
        {
            jwtValid = await this.jwtValidator.ValidateAsync(token, req.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "AAD metadata fetch failed; treating as transient");
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }

        if (!jwtValid)
        {
            return new UnauthorizedResult();
        }

        var properties = new Dictionary<string, object>
        {
            ["OperationId"] = envelope.OperationId,
            ["Action"] = envelope.Action ?? string.Empty,
            ["SubscriptionId"] = envelope.SubscriptionId ?? string.Empty,
            ["MsActivityId"] = envelope.ActivityId ?? string.Empty,
            ["ReceivedUtc"] = DateTime.UtcNow.ToString("O"),
        };

        var message = new ServiceBusMessage(rawBody)
        {
            ContentType = "application/json",
            MessageId = envelope.OperationId,
            CorrelationId = envelope.ActivityId,
        };
        foreach (var kvp in properties)
        {
            message.ApplicationProperties[kvp.Key] = kvp.Value;
        }

        try
        {
            await this.sender.SendMessageAsync(message, req.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to enqueue webhook to Service Bus; returning 503 so Microsoft retries");
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }

        this.logger.LogInformation(
            "Enqueued webhook OperationId={OperationId} Action={Action} SubscriptionId={SubscriptionId}",
            envelope.OperationId, envelope.Action, envelope.SubscriptionId);

        return new AcceptedResult();
    }

    /// <summary>
    /// Minimal projection of the inbound payload — enough for routing + structured logging.
    /// Full body is forwarded verbatim regardless of what this class captures.
    /// </summary>
    private sealed class WebhookEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? OperationId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("action")]
        public string? Action { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("subscriptionId")]
        public string? SubscriptionId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("activityId")]
        public string? ActivityId { get; set; }
    }
}
