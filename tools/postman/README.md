# Fulfillment Webhook Simulation (Postman)

Simulate Microsoft Commercial Marketplace **Fulfillment API** webhook notifications
against the CustomerSite webhook endpoint without needing a live marketplace operation.

- **Endpoint:** `POST {{baseUrl}}/api/AzureWebhook`
- **Controller:** [`AzureWebhookController`](../../src/CustomerSite/Controllers/WebHook/AzureWebhookController.cs)
- **Dispatch:** [`WebhookProcessor`](../../src/Services/WebHook/WebhookProcessor.cs) → [`WebHookHandler`](../../src/CustomerSite/WebHook/WebhookHandler.cs)

## Files

| File | Purpose |
|------|---------|
| `SaaSAccelerator.Webhooks.postman_collection.json` | The collection — one request per webhook action. |
| `SaaSAccelerator.Local.postman_environment.json` | Editable variables (base URL, subscription, plan, HMAC secret). |

Import both into Postman, then select the **SaaS Accelerator - Local** environment.

## Actions covered

Every branch of `WebhookProcessor.ProcessWebhookNotificationAsync`:

| Request | `action` | Effect in the handler |
|---------|----------|-----------------------|
| Unsubscribe | `Unsubscribe` | Local status → Unsubscribed + notifications |
| ChangePlan | `ChangePlan` | Updates plan (if accepted) |
| ChangeQuantity | `ChangeQuantity` | Updates seat quantity (if accepted) |
| Suspend | `Suspend` | Local status → Suspended |
| Reinstate | `Reinstate` | Local status → Subscribed (if accepted) |
| Renew | `Renew` | Logged only |
| Transfer (unknown/default) | `Transfer` | Falls through to `UnknownActionAsync` (logged) |

## Auth modes — the `webhookMode` variable

### `direct` (default, easiest for local dev)
Posts the JSON with no buffer headers. The endpoint only enforces JWT auth when the
app-config **`ValidateWebhookJwtToken`** is `true`, so set it to `false` (AdminSite →
app config, or the `ApplicationConfiguration` table) to accept these calls unauthenticated.

### `buffer` (mirrors the WebhookBuffer Function App)
Set `webhookMode = buffer` and `hmacSecret` = the value of
`SaaSApiConfiguration:WebhookBufferHmacSecret`. The collection pre-request script adds:

- `X-Webhook-Source: Buffer`
- `X-Signature: sha256=<hmac>` — HMAC-SHA256 (lowercase hex) of the raw body, keyed with
  `hmacSecret`, matching [`HmacSigner`](../../src/Services/Utilities/HmacSigner.cs) and
  [`BufferHmacFilter`](../../src/CustomerSite/WebHook/BufferHmacFilter.cs).

The signature is computed over the exact resolved body, so it stays valid as you edit
variables. A wrong/empty secret returns **401**.

## Prerequisites for the change to actually apply

The handlers act on the **local database**, not just the payload:

1. **The `subscriptionId` must already exist** in the accelerator DB. Unknown
   subscriptions are rejected. Copy a real subscription GUID from the CustomerSite
   list (the Fulfillment API expects **lowercase** GUIDs).
2. **ChangePlan / ChangeQuantity** apply only when app-config
   **`AcceptSubscriptionUpdates`** is `true` **and** the new value differs from the
   `subscription.*` value in the payload. Equal values simulate a *revert* (accepted);
   with `AcceptSubscriptionUpdates=false` a genuine change is *rejected* (a
   `MarketplaceException`, returned as HTTP 400).
3. **Reinstate**, when *not* accepted, calls the live Fulfillment API to PATCH the
   operation as Failure — that needs a real operation id. Test the accept path
   (`AcceptSubscriptionUpdates=true`, subscription present) for a self-contained run.

## Idempotency

The endpoint dedupes on the operation id (`Id` field). The pre-request script assigns a
**fresh `Id` on every send**, so you can re-run any request repeatedly. To deliberately
test the dedupe short-circuit, pin `opId` to a fixed GUID in the environment.

## Payload reference

Matches [`WebhookPayload`](../../src/Services/WebHook/WebhookPayload.cs). Note `Id`
(capital I) is the **operation id**, and `subscription` is the nested
[`SubscriptionWebhookResult`](../../src/Services/Models/SubscriptionWebhookResult.cs)
used by the ChangePlan/ChangeQuantity revert logic.
