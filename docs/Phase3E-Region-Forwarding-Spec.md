*2026-09-21 07:55 (Europe/London)*

# Phase 3-E — Forward SaaS Accelerator events to the tenant's home region (spec for Claude-in-VS)

> **Audience:** Claude running in Visual Studio with the **Legeris ("Read and Understood")** solution
> open (`D:\VSTFSWork\Legeris for SharePoint`). **TFVC**, **.NET Framework 4.7.2**,
> **ServiceStack + OrmLite + Newtonsoft**, MSBuild/VS. **Read the repo's root `CLAUDE.md` first.**
> Companion to [Phase3B-RAU-Receiver-Hardening-Spec.md](Phase3B-RAU-Receiver-Hardening-Spec.md) and
> [Phase3D-Cached-Status-Mode-Enablement-Spec.md](Phase3D-Cached-Status-Mode-Enablement-Spec.md).
> Sender contract reference: [Outbox-Signaling-Architecture.md](Outbox-Signaling-Architecture.md).

---

## ⚠ STATUS — 2026-09-21

**Handed to Claude-in-VS and implemented on the Legeris side.** The implementation may differ from
this document in small ways (names, log wording, exact placement); where they disagree, the checked-in
Legeris code is authoritative and this spec records the intent and the reasoning. Reconcile details
against `SaasAcceleratorEventHandler.cs` and `UnitTest1-Phase3RauPush.cs` before relying on §3 line by
line.

---

## 0. One-paragraph summary

`SaasAcceleratorEventHandler` (`Legeris.Office365.ServiceInterface/Azure/SaasAcceleratorEventHandler.cs`)
currently applies every event **in the instance that receives it**, reaching into the tenant's home
region through the `MasterDb{region}` connection strings. Change it to behave like
`ZoHoSubscriptionWebHook`: the Accelerator always posts to **one** endpoint (the USA web app); the
receiving instance resolves the tenant's **home region** and, when that is not itself, **forwards the
raw signed request** to that region's web app and **relays the response verbatim**. Only the home
region's instance ever touches the tenant's master and tenant DB. No Accelerator change is required.

---

## 1. Why — the incident of 2026-09-21

Test tenant `71150c86-ef73-422d-8fda-43879c95e0b4` (xjyg4, home region **LH**) unsubscribed its
free trial and re-purchased. The Accelerator activated the new subscription `cab6efef-…` and enqueued
`"Activated"` (outbox row 50). Every attempt got:

```
HTTP 503 {"status":"error","detail":"tenant 71150c86-… (dbSide 5de50000-2026-0905-0925-000000000000)
resolved to region LH but has no provisioned tenant DB; cannot apply pushed status 'Subscribed'"}
```

Evidence of what actually happened:

| Fact | Where seen |
|---|---|
| The receiver that answered was the **USA production** web app. | `rau-master.SaasEventLog` rows 1–3 have `ReceivedUtc` equal, to the second, to the outbox `DeliveredUtc` of the two `Unsubscribed` rows; `sddevdbelastic1Masterdb.SaasEventLog` has nothing after 14 Sep. |
| USA's deployed `Web.config` (staged 13 Sep) still maps the real tenant to ghost **0905-0925**. | `publish-stage/USA/Web.config:413-414` (also UK, Canada). |
| The tenant DB was provisioned on 20 Sep under ghost **0920-1810**. | `Tenants` row 910 → `TENANT5de50000202609201810000000000000db`; no row for 0905. |
| USA and UK region rows were re-pointed to `cab6efef` (adoption step 2b); **LH never was**. | USA `TenantRegions.Modified = 06:12:24`; LH row still `d02fd933`, `Modified = NULL`. USA's `azRegions` is `USA,UK,CA`, so LH is outside its fan-out scope. |
| The tenant DB row exists but reads `Status = cancelled`, `MarketplaceSubscriptionStatus = Unsubscribed` (state as of 06:09:58, a live pull between unsubscribe and re-purchase). | `TENANT…0920…db.Subscriptions` row 1. |

Two structural defects, both fixed by forwarding:

1. **Instance-local config is applied to another region's tenant.** The ghost mapping
   (`TestTenantRealTenantId-*`) is per-instance. Only the home region's instance can map correctly.
   In production the mapping is identity, so this is invisible there — until dev traffic transits USA.
2. **Fan-out / adoption scope is the receiving instance's, not the tenant's.** USA can never re-point
   LH. In the ZoHo model the home region does its own registration, in its own scope.

A third, non-functional smell goes away too: production configs no longer need to reach the dev
elastic server for the event path.

---

## 2. Design

### 2.1 Where the fork goes

In `Post(SaasAcceleratorEvent dto)`, keep the existing order **unchanged** up to and including
Step 1 (dedup):

```
read raw → VerifyHmac → empty/JSON checks → required-field checks → length checks
→ replay window → Step 1 dedup (local SaasEventLog)
```

Insert **Step 1b — route** between dedup and the `switch (eventType)`:

```
homeRegion = ResolveHomeRegion(eventType, body)          // §2.2
decision   = RouteDecision(thisRegion, homeRegion, isForwardedRequest)   // §2.3, pure
if decision == Forward:
    return await ForwardToRegionAsync(homeRegion, raw, Request.Headers, eventLogRow)  // §2.4
// else fall through to the existing switch — apply locally
```

The existing `switch` (fan-out, pushed-status) is **not** modified. When it runs, the home region is
this instance, so `MasterDb{azureRegion}` resolves to the local master and the cross-region reach in
`ApplyPushedSubscriptionStatusAsync` step 3 becomes a local read. Leave that code as-is; simplifying it
is out of scope.

### 2.2 Resolving the home region

| Event type | Home region source |
|---|---|
| `TenantRegionFanOut` | `body.AzureRegion` (required already; 400 if missing). This is the region the customer picked in Setup. |
| Pushed-status types (`IsPushedStatusEventType`) | `TenantRegions.AzureRegion` in the **local** master, looked up by `body.AssignedTenantId`; if the event carries no tenant id, by the existing `ResolveTenantBySubscriptionId(saasSubscriptionId)` scan. |

If a pushed-status event cannot be resolved to a region, do **not** forward anywhere: fall through to
the existing switch, which already returns the correct onboarding-transient 503 ("tenant not
resolvable…"). Region is set-once (`RegisterInRegionAsync` never changes `AzureRegion`), so the
replicated row is safe to route on even when its `SubscriptionId` is stale.

### 2.3 Route decision (pure, tested)

```csharp
internal enum Route { Local, Forward }

internal static Route RouteDecision(string thisRegion, string homeRegion, bool isForwardedRequest)
{
    if (isForwardedRequest) return Route.Local;                       // loop guard, §2.5
    if (string.IsNullOrWhiteSpace(homeRegion)) return Route.Local;    // unresolved → existing 503 path
    return string.Equals(thisRegion, homeRegion, StringComparison.OrdinalIgnoreCase)
        ? Route.Local
        : Route.Forward;
}
```

`thisRegion` is `ThisAzRegion()`. It is already a hard configuration error when empty
(`FanOutSaasTenantRegionAsync` logs Critical); treat empty the same way here: log Critical and return
503 `{"status":"receiver-not-configured","detail":"azRegion not set"}` **before** routing.

### 2.4 Forwarding

**Target URL.** New AppSettings, one per region, parallel to the ZoHo `azRegion_{X}` set but
pointing at the SaaS event route:

```xml
<add key="azRegionSaasEvent_USA" value="https://readandunderstood.azurewebsites.net/api/saasaccelerator/event" />
<add key="azRegionSaasEvent_UK"  value="https://readandunderstood-uk.azurewebsites.net/api/saasaccelerator/event" />
<add key="azRegionSaasEvent_CA"  value="https://readandunderstood-ca.azurewebsites.net/api/saasaccelerator/event" />
<add key="azRegionSaasEvent_DEV" value="https://readandunderstood-dev.azurewebsites.net/api/saasaccelerator/event" />
<!-- LH: the ngrok tunnel to your localhost, same convention as azRegion_LH. Leave empty when not tunnelling. -->
<add key="azRegionSaasEvent_LH"  value="" />
```

Explicit keys rather than string-replacing the ZoHo URL: the two routes are different services and
the LH tunnel may differ. `AU` is retired and is not listed.

**Missing or empty URL for the home region → 503**, body
`{"status":"forward-not-configured","detail":"no azRegionSaasEvent_<region> for tenant <id>"}`,
logged **Critical**. This is a deliberate departure from `ZoHoSubscriptionWebHook`, which falls
through to local processing when the LH URL is empty. For SaaS events, local processing of another
region's tenant is exactly the failure in §1. 503 keeps the sender's outbox retrying (up to ~52 h)
while the tunnel is brought up or the key is set.

**Request.** POST the **raw body bytes exactly as received** (the HMAC covers the raw body, so no
re-serialisation) with these headers copied from the inbound request:

| Header | Copy | Note |
|---|---|---|
| `Content-Type` | `application/json; charset=utf-8` | |
| `X-Signature` | verbatim | Target verifies with its own ring — see §2.6. |
| `X-Event-Type` | verbatim | Informational. |
| `X-Idempotency-Key` | verbatim | Informational (target derives its own key from the body). |
| `X-Forwarded-Region` | **add**, value `thisRegion` | Loop guard, §2.5. |

Use `HttpClient` with **redirect following disabled** (`AllowAutoRedirect = false`) and a timeout of
**60 s**. The sender's `LegerisSignalingDispatcher` uses the default 100 s `HttpClient` timeout; the
forward must fail first so the sender sees a real 503, not a socket timeout.

**Response relay.** Return the target's **status code and body unchanged**, `Content-Type: application/json`.
Do not reinterpret. The sender's classification already does the right thing with each code
(`LegerisSignalingDispatcher.cs`): 2xx delivered; 3xx, 404, 408, 429, 5xx transient; other 4xx
dead-letter. In particular:

- **Do not swallow 404.** ZoHo swallows it to stop ZoHo's retry storm. Here the outbox is ours and
  bounded, and a dropped ngrok tunnel answers 404 on a resolvable domain; the sender treats 404 as
  transient by design.
- **Transport failures** (DNS, refused, timeout, TLS) → 503
  `{"status":"forward-failed","detail":"<exception message>","region":"<home>"}`, logged
  **Warning** with the region named. The same onboarding-window escalation used by
  `PushFailureSeverity` is not needed here; a region that cannot be reached is an ops fault from the
  first attempt but is routinely a zip-deploy swap, so Warning per attempt, and rely on the sender's
  dead-letter to surface a persistent one.

**Persisting `SaasEventLog` at the forwarder.** Persist the existing `eventLogRow` **only when the
target answered 2xx**, exactly as the local path persists only after side effects succeed. Rationale:
a retry after a lost 2xx then short-circuits at the forwarder's dedup (200 `already-applied`) instead
of re-forwarding; a failed forward leaves no row, so the retry re-forwards. No schema change: the log
line records the forward and the target region; the row itself is the same shape as today. The target
persists its own row through the normal path.

Log line on success (Information):
`SaasAcceleratorEvent {idempotencyKey}: forwarded {eventType} for tenant {id} to {region} -> HTTP {code}`.

### 2.5 Loop guard

A request carrying `X-Forwarded-Region` is **never forwarded again**, whatever the local resolution
says (`RouteDecision` returns `Local`). If the local resolution disagrees with the forwarder's choice
(possible only through replication lag on a brand-new `TenantRegions` row), log **Warning** naming
both regions and apply locally anyway — the forwarder read the same replicated data and one hop is
the maximum.

### 2.6 HMAC across regions

The target verifies `X-Signature` against **its own** key ring
(`SaaSAcceleratorHmacSecret.<kid>` + `SaaSAcceleratorHmacCurrentKeyId`, via `HmacKeyRing.FromConfig`).
The Accelerator signs with a single `LegerisSignalingHmacSecret`. Therefore **every regional web app
must hold the Accelerator's current secret in its ring under the same key id.** This is a
deployment precondition, not code. In the live regions the `SaaSAcceleratorHmacSecret.k1` entry is
**deliberately absent from `Web.config`**; the value is supplied as an App Service application
setting (which overrides AppSettings without a deploy), and only `SaaSAcceleratorHmacCurrentKeyId`
ships in the file. Keep it that way. **Confirmed 2026-09-21: the k1 value is already the same in
every live region**, so a forwarded body verifies at the target as-is. No key work is needed for
cut-over; the only remaining check is that LH/DEV instances use the same value when they receive
forwards (§4).

Re-signing per region with a region-specific secret was considered and rejected for now: it adds a
second key-management surface for no security gain, since all regions are ours and the raw-body
signature already proves origin end to end. Revisit only if regional secrets must differ.

### 2.7 What does not change

- The Accelerator: `LegerisSignalingEndpointUrl` stays a single URL and should point permanently at
  the **USA** production event endpoint, matching "ZoHo webhook always hits USA first". Both the outbox
  drain and `AzureRegionService.SaveRegionAndFanOutAsync` post there, so `TenantRegionFanOut` is
  forwarded by the same rule.
- `FanOutSaasTenantRegionAsync`, adoption (`AdoptReplacementSubscriptionId`), the ordering guards,
  `ClassifyPushOutcome`, the reconcile handlers (`SaaSInitialiseTenantRegions`,
  `SaaSInitialiseSubscriptions`) — untouched. They now always run in the home region.
- The isolation-scope rule (`IsRegionInThisIsolationScope`) — untouched. A dev-homed tenant is
  forwarded to LH/DEV, whose `azRegions` (`LH,DEV,USA,UK,CA`) then registers it everywhere it does
  today.

---

## 3. Implementation checklist

Files: `Legeris.Office365.ServiceInterface/Azure/SaasAcceleratorEventHandler.cs`,
`Legeris.Office365/Legeris.Office365Web/Web.config` (+ each `Web.Release *.config` transform that
carries `azRegion_*`), `Legeris.Office365.Tests/UnitTest1-Phase3RauPush.cs`.

1. Add `Route` enum and `internal static Route RouteDecision(...)` (§2.3) next to
   `IsPushedStatusEventType`.
2. Add `internal static string ForwardUrlFor(string region, Func<string,string> getAppSetting)` reading
   `azRegionSaasEvent_{region}`; returns null when missing/blank.
3. Add `private async Task<object> ForwardToRegionAsync(string region, string raw, NameValueCollection
   inboundHeaders, SaasEventLog eventLogRow)` implementing §2.4 (client, headers, relay, persist-on-2xx,
   logging). A single static `HttpClient` with `AllowAutoRedirect = false`, `Timeout = 60 s`.
4. Add `private string ResolveHomeRegion(string eventType, SaasAcceleratorEventBody body)` (§2.2),
   reusing `ResolveTenantBySubscriptionId` for the no-tenant-id case. Must never throw; return null on
   any miss so the existing 503 path answers.
5. Wire Step 1b into `Post` immediately after the dedup block. Read
   `Request.Headers["X-Forwarded-Region"]` for `isForwardedRequest`.
6. `Web.config`: add the five `azRegionSaasEvent_*` keys (§2.4) beside the `azRegion_*` block, with the
   same LH/ngrok comment. Mirror into every release transform that currently sets `azRegion_*`.
7. **Remove `TestTenantRealTenantId-*` and `TestTenantId-*` entries from the USA, UK and Canada
   transforms.** They are dev-only and were the proximate cause in §1. (Whether to also drop
   `MasterDbLH` / `MasterDbDEV` connection strings from production is a separate tidy-up; not required
   by this change.)
8. Tests (NUnit, extend `UnitTest1-Phase3RauPush.cs`, currently 42 tests):
   - `RouteDecision`: same region → Local; different → Forward; forwarded header → Local even when
     different; null/empty home → Local; case-insensitive region compare.
   - `ForwardUrlFor`: present → URL; missing key → null; blank value → null.
   - `ResolveHomeRegion`: `TenantRegionFanOut` uses `body.AzureRegion`; status event uses the
     `TenantRegions` row (inject the lookup as a `Func<Guid, string>` so no DB is needed).
   - Relay: a small pure `RelayResponse(HttpStatusCode code, string body)` → `HttpResult` with the same
     code/body; transport exception → 503 `forward-failed`. Keep the HTTP call itself out of the test.
9. Build both targets; all tests green; bump `AssemblyFileVersion` per the repo convention (UK local
   time).

Nothing here touches the database schema.

---

## 4. Rollout order (matters)

1. Deploy the new build to **every** regional web app (USA, UK, CA, DEV; and run it locally for LH),
   each with the `azRegionSaasEvent_*` keys. The live regions already share one
   `SaaSAcceleratorHmacSecret.k1` application setting (§2.6); make sure the local LH run and the DEV
   app use that same value, since they will now receive forwards signed with it.
   Until every region accepts `X-Forwarded-Region` and verifies the signature, a forward from USA will
   401 at the target and dead-letter at the sender — so the regions go first.
2. Set `SaaSApiConfiguration__LegerisSignalingEndpointUrl` on `rau-portal` to the USA event endpoint
   (it already points there today; confirm). Saving the setting recycles the app; no redeploy.
3. Send a `TenantRegionFanOut` and an `Activated` from the Postman lifecycle collection for an
   LH-homed test tenant and confirm: USA `SaasEventLog` gains a row, LH `SaasEventLog` gains a row, LH
   tenant DB `Subscriptions` row flips, USA answered the sender with the LH status code.

---

## 5. Repairing tenant 71150c86 after deployment

The forward fixes future events. The stuck state from §1 needs a one-off repair, in this order:

1. **Re-point the LH region row** (or wait for the daily `SaaSInitialiseTenantRegions` pass, which
   will do the same and log the expected single drift line):
   ```sql
   -- sddevdbelastic1Masterdb
   UPDATE TenantRegions SET SubscriptionId = 'cab6efef-c5fd-4d75-d724-ec4312f8737a', Modified = SYSUTCDATETIME()
   WHERE TenantId = '71150c86-ef73-422d-8fda-43879c95e0b4';
   ```
2. **Retry the Activated push** once LH is reachable through the new forward:
   ```sql
   -- rauAMPSaaSDB
   UPDATE NotificationOutbox SET NextAttemptUtc = SYSUTCDATETIME(), LeasedUntilUtc = NULL, Attempts = 0
   WHERE Id = 50 AND DeliveredUtc IS NULL;
   DELETE FROM NotificationOutbox WHERE Id = 47;   -- Activated for the dead subscription d02fd933; do not replay
   ```
   The push then writes `MarketplaceSubscriptionStatus = Subscribed`, `Status = trial` (`cancelled` is in
   `OverwritableStatusSqlList`) to `TENANT…0920…db.Subscriptions`, and the setup-state gate reads
   `Subscribed` from the live pull of `cab6efef`.
3. Confirm in R&U that the tenant shows an active trial.

---

## 6. Out of scope / follow-ups

- Simplifying `ApplyPushedSubscriptionStatusAsync` now that it only ever runs in the home region
  (drop the `MasterDb{azureRegion}` indirection).
- Removing `MasterDbLH` / `MasterDbDEV` connection strings from production transforms.
- The Accelerator side is unchanged by this spec. The separate free-trial guard change (one trial per
  purchaser, `FreeTrialGuard.cs`) is in the accelerator working tree, uncommitted, and unrelated.
