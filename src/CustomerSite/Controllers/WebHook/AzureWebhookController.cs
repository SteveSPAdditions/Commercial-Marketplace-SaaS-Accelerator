// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.


using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.CustomerSite.WebHook;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Exceptions;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Marketplace.SaaS.Accelerator.Services.WebHook;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Marketplace.SaaS.Accelerator.CustomerSite.Controllers.WebHook;

/// <summary>
/// Azure Web hook.
/// </summary>
/// <seealso cref="Microsoft.AspNetCore.Mvc.ControllerBase" />
[Route("api/[controller]")]
[ApiController]
[IgnoreAntiforgeryTokenAttribute]
[ServiceFilter(typeof(BufferHmacFilter))]
public class AzureWebhookController : ControllerBase
{
    /// <summary>
    /// The application log repository.
    /// </summary>
    private readonly IApplicationLogRepository applicationLogRepository;

    /// <summary>
    /// The subscriptions repository.
    /// </summary>
    private readonly ISubscriptionsRepository subscriptionsRepository;

    /// <summary>
    /// The current configuration
    /// </summary>
    private readonly SaaSApiClientConfiguration configuration;

    /// <summary>
    /// The plan repository.
    /// </summary>
    private readonly IPlansRepository planRepository;

    /// <summary>
    /// The subscriptions log repository.
    /// </summary>
    private readonly ISubscriptionLogRepository subscriptionsLogRepository;

    /// <summary>
    /// The web hook processor.
    /// </summary>
    private readonly IWebhookProcessor webhookProcessor;

    /// <summary>
    /// The application log service.
    /// </summary>
    private readonly ApplicationLogService applicationLogService;

    /// <summary>
    /// The subscription service.
    /// </summary>
    private readonly SubscriptionService subscriptionService;

    /// <summary>
    /// The JWT token validation.
    /// </summary>
    private readonly ValidateJwtToken validateJwtToken;

    /// <summary>
    /// The ApplicationConfig Repository.
    /// </summary>
    private readonly IApplicationConfigRepository applicationConfigRepository;

    /// <summary>
    /// The ApplicationConfig service.
    /// </summary>
    private readonly ApplicationConfigService applicationConfigService;

    /// <summary>
    /// The webhook operation log repository.
    /// </summary>
    private readonly IWebhookOperationLogRepository webhookOperationLogRepository;

    /// <summary>
    /// The logger. Emits structured, Stage-tagged telemetry to Application Insights so a
    /// live webhook failure can be attributed to the in-project auth check, the local
    /// processing, or an outbound Microsoft Fulfillment API call.
    /// </summary>
    private readonly ILogger<AzureWebhookController> logger;


    /// <summary>
    /// Initializes a new instance of the <see cref="AzureWebhookController"/> class.
    /// </summary>
    /// <param name="applicationLogRepository">The application log repository.</param>
    /// <param name="webhookProcessor">The Web hook log repository.</param>
    /// <param name="subscriptionsLogRepository">The subscriptions log repository.</param>
    /// <param name="planRepository">The plan repository.</param>
    /// <param name="subscriptionsRepository">The subscriptions repository.</param>
    /// <param name="configuration">The SaaSApiClientConfiguration from ENV</param>
    /// <param name="validateJwtToken">The validateJwtToken utility</param>
    /// <param name="applicationConfigRepository">The application config repository</param>
    /// <param name="webhookOperationLogRepository">The webhook operation log repository</param>
    /// <param name="logger">The logger</param>
    public AzureWebhookController(IApplicationLogRepository applicationLogRepository,
                                  IWebhookProcessor webhookProcessor,
                                  ISubscriptionLogRepository subscriptionsLogRepository,
                                  IPlansRepository planRepository,
                                  ISubscriptionsRepository subscriptionsRepository,
                                  SaaSApiClientConfiguration configuration,
                                  ValidateJwtToken validateJwtToken,
                                  IApplicationConfigRepository applicationConfigRepository,
                                  IWebhookOperationLogRepository webhookOperationLogRepository,
                                  ILogger<AzureWebhookController> logger)
    {
        this.applicationLogRepository = applicationLogRepository;
        this.subscriptionsRepository = subscriptionsRepository;
        this.configuration = configuration;
        this.planRepository = planRepository;
        this.subscriptionsLogRepository = subscriptionsLogRepository;
        this.webhookProcessor = webhookProcessor;
        this.applicationLogService = new ApplicationLogService(this.applicationLogRepository);
        this.subscriptionService = new SubscriptionService(this.subscriptionsRepository, this.planRepository);
        this.validateJwtToken = validateJwtToken;
        this.applicationConfigRepository = applicationConfigRepository;
        this.applicationConfigService = new ApplicationConfigService(this.applicationConfigRepository);
        this.webhookOperationLogRepository = webhookOperationLogRepository;
        this.logger = logger;
    }

    /// <summary>
    /// Posts the specified request.
    /// </summary>
    /// <param name="request">The request.</param>
    public async Task<IActionResult> Post(WebhookPayload request)
    {
        // Attach correlating properties to every telemetry item emitted while handling this
        // webhook, so a failure in Application Insights carries the action / subscription /
        // operation without having to cross-reference the DB ApplicationLog table.
        using var logScope = this.logger.BeginScope(new Dictionary<string, object>
        {
            ["Action"] = request?.Action.ToString() ?? "(null)",
            ["SubscriptionId"] = request?.SubscriptionId ?? Guid.Empty,
            ["OperationId"] = request?.OperationId ?? Guid.Empty,
        });

        try
        {
            await this.applicationLogService.AddApplicationLog("The azure Webhook Triggered.").ConfigureAwait(false);

            // The BufferHmacFilter has already authenticated the call when it carries the
            // X-Webhook-Source: Buffer header. Skip JWT validation in that case — the
            // Function App authenticated the Microsoft caller before forwarding.
            var fromBuffer = string.Equals(
                this.HttpContext.Request.Headers["X-Webhook-Source"].ToString(),
                "Buffer",
                StringComparison.OrdinalIgnoreCase);

            var appConfigValueConversion = bool.TryParse(this.applicationConfigService.GetValueByName("ValidateWebhookJwtToken"), out bool appConfigValue);

            if (!fromBuffer && appConfigValueConversion && appConfigValue)
            {
                await this.applicationLogService.AddApplicationLog("Validating the JWT token.").ConfigureAwait(false);

                // Extract the bearer token explicitly so a missing/malformed Authorization
                // header is reported as such, instead of surfacing as an opaque IndexOutOfRange.
                var authHeader = this.HttpContext.Request.Headers["Authorization"].ToString();
                var headerParts = authHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Shape-only diagnostic (never the token itself — it's a live bearer credential).
                // Reveals mangling that survives a valid variable: a double "Bearer ", a token with
                // an embedded space (only the first chunk reaches parts[1]), an unresolved
                // {{placeholder}}, etc. Emitted as a customDimension for App Insights querying.
                var tokenShape = DescribeTokenShape(authHeader, headerParts);

                if (headerParts.Length < 2 || !string.Equals(headerParts[0], "Bearer", StringComparison.OrdinalIgnoreCase))
                {
                    const string reason = "MissingOrMalformedAuthorizationHeader";
                    this.logger.LogWarning(
                        "Webhook rejected at {Stage}: {Reason}. Authorization header was not a 'Bearer <token>' value. TokenShape={TokenShape}",
                        "WebhookAuth", reason, tokenShape);
                    await this.applicationLogService.AddApplicationLog($"Jwt token validation failed: {reason}. {tokenShape}").ConfigureAwait(false);
                    return new UnauthorizedResult();
                }

                try
                {
                    await validateJwtToken.ValidateTokenAsync(headerParts[1]);
                }
                catch (Exception e)
                {
                    var reason = DescribeAuthFailure(e);

                    // Warning (not Error): a rejected caller is an expected security outcome,
                    // but Stage=WebhookAuth + Reason makes it queryable in App Insights and
                    // clearly attributable to the in-project check (never a Microsoft call).
                    this.logger.LogWarning(
                        e,
                        "Webhook rejected at {Stage}: {Reason}. Token validation failed. TokenShape={TokenShape}",
                        "WebhookAuth", reason, tokenShape);
                    await this.applicationLogService.AddApplicationLog($"Jwt token validation failed [{reason}]: {e.Message}. {tokenShape}").ConfigureAwait(false);

                    return new UnauthorizedResult();
                }
            }

            if (request != null)
            {
                // Idempotency short-circuit: if we have already processed this OperationId,
                // log and return 200 without re-running handlers. Microsoft + the buffer can
                // both deliver the same OperationId more than once.
                if (request.OperationId != Guid.Empty)
                {
                    var existing = this.webhookOperationLogRepository.Get(request.OperationId);
                    if (existing != null)
                    {
                        await this.applicationLogService.AddApplicationLog(
                            $"Webhook OperationId {request.OperationId} already processed at {existing.ReceivedUtc:O} ({existing.ResultStatus}); skipping.").ConfigureAwait(false);
                        return Ok();
                    }
                }

                var json = JsonSerializer.Serialize(request);
                await this.applicationLogService.AddApplicationLog("Webhook Serialize Object " + json).ConfigureAwait(false);
                await this.webhookProcessor.ProcessWebhookNotificationAsync(request, configuration).ConfigureAwait(false);

                if (request.OperationId != Guid.Empty)
                {
                    this.webhookOperationLogRepository.Save(new DataAccess.Entities.WebhookOperationLog
                    {
                        OperationId = request.OperationId,
                        ReceivedUtc = DateTime.UtcNow,
                        Action = request.Action.ToString(),
                        SubscriptionId = request.SubscriptionId,
                        ResultStatus = "Processed",
                    });
                }

                return Ok();
            }
            throw new MarketplaceException("Request payload is null.");
        }
        catch (MarketplaceException ex)
        {
            // Business-rule rejection (e.g. plan change refused by config, subscription not in
            // DB). Expected outcome — Warning, Stage=Processing, returned to Microsoft as 400.
            this.logger.LogWarning(
                ex,
                "Webhook returned 400 at {Stage}: {Reason}.",
                "Processing", ex.Message);
            await this.applicationLogService.AddApplicationLog(
                    $"A Marketplace exception occurred while attempting to process a webhook notification: [{ex.Message}].")
                .ConfigureAwait(false);
            return BadRequest();
        }
        catch (Exception ex)
        {
            // Unexpected failure. This is where an outbound Microsoft Fulfillment API failure
            // (e.g. the Reinstate reject PATCH) also surfaces — the handler logs Stage=FulfillmentApi
            // first, so the two are distinguishable in App Insights.
            this.logger.LogError(
                ex,
                "Webhook returned 500 at {Stage}: {Message}.",
                "Processing", ex.Message);
            await this.applicationLogService.AddApplicationLog(
                    $"An error occurred while attempting to process a webhook notification: [{ex.Message}].")
                .ConfigureAwait(false);
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Maps a token-validation exception to a short, queryable reason so failures can be
    /// triaged in Application Insights without parsing the raw IDX message.
    /// </summary>
    /// <param name="e">The exception thrown by the JWT validation path.</param>
    /// <returns>A stable, human-readable failure category.</returns>
    /// <summary>
    /// Builds a SHAPE-ONLY description of the Authorization header for diagnostics — never the
    /// token value (it is a live bearer credential). Surfaces the exact reasons a token that is
    /// valid in the client still fails server-side: double "Bearer ", an embedded space that
    /// truncates parts[1], an unresolved {{placeholder}}, or a non-JWT segment count.
    /// </summary>
    private static string DescribeTokenShape(string authHeader, string[] parts)
    {
        var scheme = parts.Length > 0 ? parts[0] : string.Empty;
        var token = parts.Length >= 2 ? parts[1] : string.Empty;
        var segments = string.IsNullOrEmpty(token) ? 0 : token.Split('.').Length;

        // First few chars only — enough to tell 'eyJ…' (real JWT) from 'Bearer', '{{acc…' or
        // '"eyJ…' (quoted), without exposing the payload/signature.
        var preview = token.Length == 0
            ? "(empty)"
            : token.Substring(0, Math.Min(6, token.Length));

        return $"authHeaderLen={authHeader.Length}, spaceParts={parts.Length}, scheme='{scheme}', "
             + $"tokenLen={token.Length}, dotSegments={segments}, tokenPreview='{preview}'";
    }

    private static string DescribeAuthFailure(Exception e)
    {
        // IDX12741: value handed to the JWT handler is not a JWT (not 3/5 dot-separated
        // segments). Typically a non-JWT bearer value — e.g. a manual Postman token.
        if (e is SecurityTokenMalformedException || (e.Message?.Contains("IDX12741") ?? false))
        {
            return "MalformedToken: Authorization bearer value is not a JWT";
        }

        return e switch
        {
            SecurityTokenExpiredException => "Expired: token lifetime elapsed",
            SecurityTokenInvalidSignatureException => "InvalidSignature: signing key did not validate",
            SecurityTokenInvalidAudienceException => "AudienceMismatch: 'aud' is not the configured ClientId",
            SecurityTokenValidationException => $"ClaimMismatch: {e.Message}",
            _ => $"TokenValidationFailed: {e.GetType().Name}",
        };
    }
}