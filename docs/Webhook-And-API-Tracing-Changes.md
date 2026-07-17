# Webhook & Marketplace API Tracing — Change Summary

**Date:** 2026-07-16 / 2026-07-17
**Status:** Implemented, full solution builds clean (`dotnet build src/SaaSAccelerator.sln`), `Services.Test` passes.
**Not committed yet** — working tree changes only.

## Why

A `POST /api/AzureWebhook` (Postman ChangePlan test) returned **401 Unauthorized** with
`IDX12741: JWT must have three segments (JWS) or five segments (JWE)`. Application Insights
could not tell us *where* a webhook failed — the in-project JWT check, local processing, or an
outbound Microsoft Marketplace API call. All meaningful detail went to the DB `ApplicationLog`
table (unstructured, HTML-encoded), never to AI, and the controller swallowed exceptions and
returned bare status codes.

## Root cause of the original 401

The request hit the JWT-validation branch (`ValidateWebhookJwtToken` app-config = `true`,
no `X-Webhook-Source: Buffer` header) with a bearer value that **isn't a real Azure AD JWT**.
IDX12741 fires before any signature/audience check — the string simply isn't 3 dot-separated
segments. **ChangePlan never calls Microsoft** in this accelerator; it accepts via HTTP 200 /
rejects via 400 against the local DB. Only the Reinstate-reject path makes an outbound call.

For local testing: set `ValidateWebhookJwtToken = false`, or use buffer/HMAC mode.

## The tracing model (Stage tag)

Every failure now emits structured telemetry with a `Stage` customDimension:

| Stage | Covers | Reaches App Insights |
|-------|--------|----------------------|
| `WebhookAuth` | In-project JWT/HMAC check (the 401 lands here) | Yes |
| `Processing` | Local webhook business logic (400 = rule rejection, 500 = unexpected) | Yes |
| `MarketplaceApi` | All outbound Fulfillment + Metering calls; carries `MarketplaceAction` + `StatusCode` | Yes (web hosts) |

### App Insights query

```kusto
traces
| extend Stage = tostring(customDimensions.Stage)
| where Stage in ("WebhookAuth","Processing","MarketplaceApi")
| project timestamp, Stage,
          MarketplaceAction = customDimensions.MarketplaceAction,
          StatusCode = customDimensions.StatusCode, message
```

## Files changed

### Turn 1 — Webhook path

- **`src/Services/Utilities/ValidateJwtToken.cs`**
  Tenant/appId mismatches now throw `SecurityTokenValidationException` (was bare `Exception`)
  so they are categorizable, distinct from malformed/expired/signature failures.

- **`src/CustomerSite/Controllers/WebHook/AzureWebhookController.cs`**
  - Injected `ILogger<AzureWebhookController>`.
  - Wrapped `Post` in a logging scope carrying `Action` / `SubscriptionId` / `OperationId`.
  - Bearer token now extracted explicitly; missing/malformed `Authorization` header is
    reported as `MissingOrMalformedAuthorizationHeader` (was a swallowed `IndexOutOfRange`).
  - JWT failures categorized via new `DescribeAuthFailure` helper — e.g.
    `MalformedToken` (IDX12741), `Expired`, `InvalidSignature`, `AudienceMismatch`,
    `ClaimMismatch`. Logged `LogWarning` with `Stage=WebhookAuth`.
  - Catch blocks now emit real exception telemetry: `MarketplaceException` -> `LogWarning`
    `Stage=Processing` (400); other -> `LogError` `Stage=Processing` (500).

- **`src/CustomerSite/WebHook/WebhookHandler.cs`**
  Reinstate-reject outbound PATCH failure now logs `LogError` with `Stage=FulfillmentApi`
  (added `ILogger<WebHookHandler>` from the existing `ILoggerFactory`).

### Turn 2 — All Fulfillment + Metering API paths

- **`src/Services/Contracts/ILogger.cs`**
  Added `void MarketplaceApiError(string marketplaceAction, int statusCode, Exception ex)`.

- **`src/Services/Utilities/FulfillmentApiClientLogger.cs`**
  Implemented `MarketplaceApiError` -> `LogError` with template
  `Stage={Stage} MarketplaceAction={MarketplaceAction} StatusCode={StatusCode}`
  (real customDimensions).

- **`src/Services/Utilities/SaaSClientLogger.cs`**
  - **Fixed a real blind spot:** was console-only (own `LoggerFactory`), so all metering
    telemetry and several AdminSite controllers never reached AI.
  - Added a DI constructor `SaaSClientLogger(ILogger<T>)` (reaches AI); kept the
    parameterless console-only constructor as a fallback for hosts with no logging pipeline.
  - Implemented `MarketplaceApiError` (same template as above).

- **`src/Services/Services/BaseApiService.cs`**
  `ProcessErrorResponse` (the single funnel for every Fulfillment + Metering error) now calls
  `this.Logger?.MarketplaceApiError(marketplaceAction.ToString(), statusCode, ex)`.
  One edit instruments all API methods.

- **`src/AdminSite/Startup.cs`**
  Metering registration converted to a factory that resolves the DI logger:
  `sp => new MeteredBillingApiService(..., new SaaSClientLogger<MeteredBillingApiService>(sp.GetRequiredService<ILogger<MeteredBillingApiService>>()))`
  so AdminSite metering telemetry reaches AI.

## Known remaining gap (NOT done)

**`src/MeteredTriggerJob/Program.cs`** registers **no logging pipeline** (bare
`ServiceCollection`, no `AddLogging` / App Insights). Its metering registration was
intentionally left as the parameterless `new SaaSClientLogger<MeteredBillingApiService>()`
console fallback — **no regression**, but the scheduled metering job's telemetry still goes
only to the WebJob log stream, not App Insights.

To close it (separate follow-up):
- Add `Microsoft.ApplicationInsights.WorkerService` + `AddLogging` to the job's services.
- Add explicit `TelemetryClient.Flush()` + drain before the process exits (WebJobs exit fast
  and drop buffered telemetry otherwise).
- Needs `APPLICATIONINSIGHTS_CONNECTION_STRING` in the job's config (mind the 500.30 trap
  noted for the web hosts — empty/malformed connection string is fatal).

## Verify after restart

```bash
dotnet build src/SaaSAccelerator.sln
dotnet test src/Services.Test/Services.Test.csproj
```
