// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Exceptions;
using Marketplace.SaaS.Accelerator.Services.Models;
using Marketplace.SaaS.Accelerator.Services.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Marketplace.SaaS.Accelerator.CustomerSite.Controllers.Api;

/// <summary>
/// Machine-to-machine JSON API for initiating a plan change - e.g. the trial -> paid
/// "Upgrade" button in the Read and Understood React app. It mirrors the browser-only
/// <c>HomeController.ChangeSubscriptionPlan</c> action but is reachable by an external
/// backend because it:
///   - authenticates with a bearer token for the runtime app (RuntimeAppClientId) instead
///     of the OIDC sign-in cookie + antiforgery token, and
///   - returns 202 + operationId immediately instead of blocking on the operation poll.
/// The work itself is the same OUTBOUND Fulfillment API call (via the ClientId client
/// credentials); Microsoft confirms the result asynchronously via the AzureWebhook.
/// </summary>
[ApiController]
[Route("api/subscriptions")]
[IgnoreAntiforgeryToken]
[Authorize(AuthenticationSchemes = SubscriptionApiAuthScheme)]
public class SubscriptionApiController : ControllerBase
{
    /// <summary>Named authentication scheme wired up in Startup for this API.</summary>
    public const string SubscriptionApiAuthScheme = "SubscriptionApi";

    private readonly IFulfillmentApiService apiService;
    private readonly IPlansRepository planRepository;
    private readonly SubscriptionService subscriptionService;
    private readonly ILogger<SubscriptionApiController> logger;

    public SubscriptionApiController(
        IFulfillmentApiService apiService,
        ISubscriptionsRepository subscriptionsRepository,
        IPlansRepository planRepository,
        ILogger<SubscriptionApiController> logger)
    {
        this.apiService = apiService;
        this.planRepository = planRepository;
        this.subscriptionService = new SubscriptionService(subscriptionsRepository, planRepository);
        this.logger = logger;
    }

    /// <summary>
    /// Initiates a plan change on the given subscription. Returns 202 with the marketplace
    /// operationId; poll <see cref="GetOperation"/> (or wait for the webhook) for completion.
    /// </summary>
    [HttpPost("{subscriptionId:guid}/changeplan")]
    public async Task<IActionResult> ChangePlan(Guid subscriptionId, [FromBody] ChangePlanApiRequest request)
    {
        using var logScope = this.logger.BeginScope(new Dictionary<string, object>
        {
            ["Action"] = "ChangePlan(Api)",
            ["SubscriptionId"] = subscriptionId,
            ["CallerAppId"] = this.CallerAppId(),
        });

        if (subscriptionId == default)
        {
            return this.ValidationError("subscriptionId is required.");
        }

        var planId = request?.PlanId?.Trim();
        if (string.IsNullOrEmpty(planId))
        {
            return this.ValidationError("planId is required.");
        }

        // Load the subscription (active only). GetSubscriptionsBySubscriptionId returns an
        // empty result (Id == default) rather than null when the id is unknown.
        var subscription = this.subscriptionService.GetSubscriptionsBySubscriptionId(subscriptionId, includeUnsubscribed: false);
        if (subscription == null || subscription.Id == default)
        {
            this.logger.LogWarning("Change-plan rejected at {Stage}: subscription not found.", "Processing");
            return this.Problem(statusCode: StatusCodes.Status404NotFound, title: "Subscription not found",
                detail: $"No active subscription {subscriptionId}.");
        }

        // Eligibility: a plan change is only valid on an active (Subscribed) subscription, and
        // only when the target plan actually differs and exists for this deployment. Without
        // these guards the endpoint would be an any-subscription mutator by GUID.
        if (subscription.SubscriptionStatus != SubscriptionStatusEnumExtension.Subscribed)
        {
            return this.Problem(statusCode: StatusCodes.Status409Conflict, title: "Subscription not upgradeable",
                detail: $"Subscription is '{subscription.SubscriptionStatus}'; must be 'Subscribed' to change plan.");
        }

        if (string.Equals(subscription.PlanId, planId, StringComparison.OrdinalIgnoreCase))
        {
            return this.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already on requested plan",
                detail: $"Subscription is already on plan '{planId}'.");
        }

        if (this.planRepository.GetById(planId) == null)
        {
            return this.ValidationError($"Unknown planId '{planId}'.");
        }

        // Fire the OUTBOUND Fulfillment API change-plan - the same call the MVC action makes.
        try
        {
            var result = await this.apiService.ChangePlanForSubscriptionAsync(subscriptionId, planId).ConfigureAwait(false);
            if (result == null || result.OperationId == default)
            {
                this.logger.LogError("Change-plan at {Stage}: marketplace returned no operationId.", "MarketplaceApi");
                return this.Problem(statusCode: StatusCodes.Status502BadGateway, title: "No operation id",
                    detail: "The Marketplace API did not return an operation id.");
            }

            this.logger.LogInformation(
                "Change-plan initiated at {Stage}: {SubscriptionId} -> {PlanId} op {OperationId}.",
                "Processing", subscriptionId, planId, result.OperationId);

            var statusUrl = this.Url.Action(nameof(this.GetOperation), new { subscriptionId, operationId = result.OperationId });
            return this.Accepted(statusUrl, new ChangePlanApiResponse
            {
                SubscriptionId = subscriptionId,
                PlanId = planId,
                OperationId = result.OperationId,
                Status = OperationStatusEnum.InProgress.ToString(),
                StatusUrl = statusUrl,
            });
        }
        catch (MarketplaceException mex)
        {
            // Outbound marketplace failure (auth / conflict / bad request / ...). The funnel in
            // BaseApiService.ProcessErrorResponse already emitted Stage=MarketplaceApi telemetry;
            // surface a clean 502 to the caller rather than a raw 500.
            this.logger.LogWarning(mex, "Change-plan failed at {Stage}: {Message}", "MarketplaceApi", mex.Message);
            return this.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Marketplace change-plan failed", detail: mex.Message);
        }
    }

    /// <summary>
    /// Returns the status of a previously initiated operation (poll target for ChangePlan).
    /// </summary>
    [HttpGet("{subscriptionId:guid}/operations/{operationId:guid}")]
    public async Task<IActionResult> GetOperation(Guid subscriptionId, Guid operationId)
    {
        using var logScope = this.logger.BeginScope(new Dictionary<string, object>
        {
            ["Action"] = "GetOperation(Api)",
            ["SubscriptionId"] = subscriptionId,
            ["OperationId"] = operationId,
            ["CallerAppId"] = this.CallerAppId(),
        });

        if (subscriptionId == default || operationId == default)
        {
            return this.ValidationError("subscriptionId and operationId are required.");
        }

        try
        {
            var operation = await this.apiService.GetOperationStatusResultAsync(subscriptionId, operationId).ConfigureAwait(false);
            if (operation == null)
            {
                return this.Problem(statusCode: StatusCodes.Status404NotFound, title: "Operation not found",
                    detail: $"No operation {operationId} for subscription {subscriptionId}.");
            }

            return this.Ok(new OperationStatusApiResponse
            {
                SubscriptionId = subscriptionId,
                OperationId = operationId,
                Status = operation.Status.ToString(),
            });
        }
        catch (MarketplaceException mex)
        {
            this.logger.LogWarning(mex, "Get-operation failed at {Stage}: {Message}", "MarketplaceApi", mex.Message);
            return this.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Marketplace operation lookup failed", detail: mex.Message);
        }
    }

    /// <summary>App id (client) of the calling token, for correlating telemetry.</summary>
    private string CallerAppId()
        => this.User?.FindFirstValue("azp")
           ?? this.User?.FindFirstValue("appid")
           ?? "(unknown)";

    private IActionResult ValidationError(string detail)
        => this.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail);
}

/// <summary>Request body for <c>POST /api/subscriptions/{id}/changeplan</c>.</summary>
public class ChangePlanApiRequest
{
    /// <summary>The target (paid) plan id to move the subscription to.</summary>
    public string PlanId { get; set; }
}

/// <summary>202 response body for a successfully initiated plan change.</summary>
public class ChangePlanApiResponse
{
    public Guid SubscriptionId { get; set; }
    public string PlanId { get; set; }
    public Guid OperationId { get; set; }
    public string Status { get; set; }
    public string StatusUrl { get; set; }
}

/// <summary>Response body for an operation-status poll.</summary>
public class OperationStatusApiResponse
{
    public Guid SubscriptionId { get; set; }
    public Guid OperationId { get; set; }
    public string Status { get; set; }
}
