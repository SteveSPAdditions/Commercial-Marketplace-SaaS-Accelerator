# Phase 3 — RAU push-authoritative subscription status (spec for Claude-in-VS)

> **Audience:** Claude running in Visual Studio with the **Legeris ("Read and Understood")** solution open
> (`D:\VSTFSWork\Legeris for SharePoint`). This repo is **TFVC** (not git), **.NET Framework 4.7.2**,
> **ServiceStack + OrmLite + Newtonsoft**, built with **MSBuild/VS** (not `dotnet`). **Read the repo's
> root `CLAUDE.md` first.** Build with MSBuild; check in with TFVC.
>
> The producer side (the SaaS Accelerator) is already done and deployed-ready. This spec implements the
> **consumer** side.

---

## ✅ IMPLEMENTATION STATUS — 2026-07-21 (RAU side, NOT yet checked into TFVC)

**B1–B4 all implemented.** Both targets build clean (.NET Framework 4.7.2 + .NET 8 AzRSvc), 26/26 NUnit
tests pass. **Nothing is checked in** — 17 files pending in the Legeris TFVC workspace.

Three defects were found in the delivered implementation *after* this spec was written and have been
fixed; a further eight are covered by
[Phase3B-RAU-Receiver-Hardening-Spec.md](Phase3B-RAU-Receiver-Hardening-Spec.md) (all now done):

| Found | Fix |
|---|---|
| **Ghost-id resolution.** `TenantRegions.TenantId` is the REAL Entra id, but `Tenants.TenantId`, the per-tenant DB name and `Subscriptions.FarmId` are the **DB-side ("ghost")** id in dev. All four Phase 3 sites looked up `MdbTenant`/`FarmId` with the real id → matched nothing → handler reported `applied` having updated zero rows. | New `DbSideTenantId()` inverse of `RealTenantId()` in `ServiceInterface\Extensions\StingExtensions.cs`, applied at all four sites. AzRSvc uses its pre-existing `SwapIfTestTenantRealTenantId()` (the Web helper needs `WebConfigurationManager`, absent on .NET 8). **The reverse lookup MUST require a Guid-shaped key suffix** — `TestTenantRealTenantId-xjyg4.sharepoint.com` is a HOST-keyed alias pointing at the same real tenant and sorts first. |
| **Multi-host config source.** `ResolveSetupStateInProcess` runs in three hosts, each passing its own `Configuration`. The cached reader used ambient `WebConfigurationManager` — Web.config (87 keys) in the Web app, but the **WebJob EXE's own app.config (6 keys, no ghost mappings)** in `Jobs.Events`. | Read the caller-supplied `configuration`, never `WebConfigurationManager`, inside `ResolveSetupStateInProcess`. (That is why the parameter exists, and why `Jobs.Events` carries its own `RealTenantId()` copy.) |
| **B3 was unreachable.** The corrector sat at the end of `SaaSInitialiseTenantRegionsHandler.Get`, behind four early returns (missing config, `FetchSnapshot` throw, malformed snapshot). Those return normally, so the orchestrator set `succeeded = true` and armed the shared 24h gate — in local dev (rau-admin unreachable) the corrector ran **never**. | Moved to `SaaSInitialiseSubscriptionsHandler` as an independent step with its own try/catch and its **own** daily gate. `_saaSInitialisedUtc` split into `_saaSRegionsInitialisedUtc` / `_saaSSubscriptionsInitialisedUtc`, each armed only on genuine success (the region step now checks `SnapshotCount >= 0`, because "didn't throw" was never proof). |

**Deviation from §2 worth knowing:** the spec said an un-provisioned tenant should be a benign no-op.
It is now **Critical + 503** — silently reporting `applied` for a write that touched nothing is what
concealed the ghost-id bug. See Phase 3-B F2/F8a for the ordering-guard rework that accompanies this.

**⚠ Deploy ordering:** Phase 3-B added `Subscriptions.MarketplaceStateAsOfUtc`. OrmLite emits explicit
column lists, so every `Select<Subscription>` fails against an un-migrated tenant DB. Deploy the **web
app (migration runner) before AzRSvc** and before any WebJob tick.

---

## 0. Objective & decisions already made (do not re-litigate)

Enable **testability of subscription lifecycle without touching Azure billing**: an operator replays a
Marketplace webhook into rau-portal, and RAU reacts as if the change were real. This requires RAU to be
**push-authoritative** — trust the status delivered in the signaling event rather than always re-pulling
Microsoft.

Locked decisions:
- **Push-authoritative:** the event's `subscriptionStatus`/`planId` are written to RAU's cached
  `Subscription.Marketplace*` columns. **Do NOT live-pull in the event handler.**
- **Gate reads cached, behind a toggle, with live fallback.** New AppSetting
  `MarketplaceStatusSource = Cached | Live` (default **`Live`** so deploy is inert until flipped). When
  `Cached`, derive `SubscribedActive` from the cached column; **fall back to the live pull when the
  cached status is null/missing.** Keep the live pull fully intact — it is the support diagnostic.
- **Reconcile corrector:** the daily reconcile refreshes the cached `Marketplace*` from a **live** pull
  (this is what makes push-authoritative self-heal), **skipping rows refreshed within a grace window**
  so a fresh test push isn't instantly clobbered.
- **Ordering guard:** every cache write is guarded by `modifiedUtc` (apply only if newer).
- **Scope:** update **all** of a tenant's per-site `Subscription` rows. `PlanChanged` is display-only.
  **No email alerting** this phase (leave `MarketplaceAlertSentUtc` / `MarketplaceAlertForStatus` null).
- **Do not fan out** these events across regions — subscription status is tenant-scoped; update only the
  tenant's home-region tenant DB.

## 1. The event contract (what the accelerator now sends)

`POST /saasaccelerator/event` (existing route), HMAC-signed exactly as `TenantRegionFanOut`:

- Headers: `X-Signature: sha256=<lowercase hex HMAC-SHA256(rawBody, SaaSAcceleratorHmacSecret)>`,
  `X-Event-Type`, `X-Idempotency-Key`.
- Body (camelCase JSON), one of four `eventType`s — **`PlanChanged`, `Unsubscribed`, `Suspended`,
  `Reinstated`**:

```jsonc
{
  "eventType":           "Unsubscribed",
  "saasSubscriptionId":  "f85be04d-...",       // Guid
  "assignedTenantId":    "71150c86-...",       // Guid; MAY be Guid.Empty if purchaser tenant unknown
  "planId":              "standard-monthly",
  "subscriptionStatus":  "Unsubscribed",       // CANONICAL: Subscribed | Unsubscribed | Suspended
  "modifiedUtc":         "2026-07-20T09:54:34.3708513Z",
  "occurredBy":          "Accelerator"
}
```

`subscriptionStatus` is already normalized to Microsoft's canonical vocabulary on the sender side
(`Suspend`→`Suspended`), so compare against `"Subscribed"`/`"Unsubscribed"`/`"Suspended"` directly.
Idempotency key shape: `{eventType}|{saasSubscriptionId:N}|{operationId:N}` (the sender puts it in
`X-Idempotency-Key`; the receiver already prefers that header).

## 2. Task B1 — receiver cases + `ApplyPushedSubscriptionStatusAsync`

**File:** `Legeris.Office365.ServiceInterface\Azure\SaasAcceleratorEventHandler.cs`.

Add four fall-through `case` labels **immediately before `default:`** in the `switch (eventType)`:

```csharp
case "PlanChanged":
case "Unsubscribed":
case "Suspended":
case "Reinstated":
    if (saasSubscriptionId == null)
        return new HttpResult("subscription event requires saasSubscriptionId", HttpStatusCode.BadRequest);

    var applyError = await ApplyPushedSubscriptionStatusAsync(
        assignedTenantId, saasSubscriptionId.Value, body.PlanId, body.SubscriptionStatus, body.ModifiedUtc);
    if (applyError != null)
    {
        // Transient / partial failure: 503 WITHOUT persisting SaasEventLog so the sender's outbox retries.
        Loging.Log($"SaasAcceleratorEvent {idempotencyKey}: {applyError}", Logger, LogSeverity.Critical);
        return new HttpResult(
            $"{{\"status\":\"error\",\"detail\":\"{applyError}\"}}", HttpStatusCode.ServiceUnavailable)
        { ContentType = "application/json" };
    }
    break;
```

> If `SaasAcceleratorEventBody` has no `SubscriptionStatus` property yet, add it (string, optional) next
> to `PlanId` in `Legeris.Office365.ServiceModel\Azure\SaasAcceleratorEvent.cs`. Newtonsoft binds
> case-insensitively, so `subscriptionStatus` → `SubscriptionStatus`.

**New private helper `ApplyPushedSubscriptionStatusAsync`** — push-authoritative, no live pull. Mirror
the existing DB-access primitives already in this file / repo:
- region-open + tenant-region read: mirror `RegisterInRegionAsync`
  (`ConfigurationManager.ConnectionStrings[$"MasterDb{region}"]` → `GetOrmLiteConnection(cs, skipUpgrade:true)`
  → `Single<TenantRegion>(t => t.TenantId == tenantId)`).
- home-region resolution: mirror `GetSetupState.cs` `lookupTenantRegion` (try local master, then iterate
  `azRegions`). `TenantRegion` rows are replicated, so any master yields `AzureRegion` + `SubscriptionId`.
- tenant-DB open + `Marketplace*` write: mirror `InitialiseSaasTenant.cs` (~L256-265) which already
  writes these columns at first-touch; open the tenant DB via that region's
  `MdbTenant.DatabaseConnectionString`.

Algorithm (return `null` on success, a short error string on failure → 503):

1. **Resolve the tenant.** If `assignedTenantId != null && != Guid.Empty`, use it. Else fall back to
   locating the `TenantRegion` row by `SubscriptionId == saasSubscriptionId` (scan; `SubscriptionId` is
   not indexed). If still unresolved → return `"tenant not resolvable"` (503 — a later reconcile/replay
   may resolve it once the tenant exists).
2. **Find the home region.** Read the tenant's `TenantRegion` (local master is enough — replicated);
   take `AzureRegion`. If the row's `SubscriptionId` is null, set it to `saasSubscriptionId` (anchor).
3. **Open the home-region tenant DB.** `MasterDb{AzureRegion}` → `MdbTenant` for this tenant →
   `DatabaseConnectionString` → open the tenant DB.
4. **Update all site rows, ordering-guarded.** For every `Subscription` where `FarmId == tenantGuid`:
   - **Guard:** if `existing.MarketplaceLastRefreshedUtc != null && existing.MarketplaceLastRefreshedUtc >= modifiedUtc`, **skip** (a newer state already applied).
   - Else `UpdateOnly`: `MarketplaceSubscriptionStatus = <pushed status>`, `MarketplacePlanId = <pushed planId>`,
     `MarketplaceLastRefreshedUtc = <modifiedUtc>` (use the event's `modifiedUtc`, not `UtcNow`, so the
     guard is consistent), and set a source marker if one exists (e.g. a `MarketplaceStatusSource = "push"`
     column — add it only if trivial; otherwise skip).
   - Leave `MarketplaceAlertSentUtc` / `MarketplaceAlertForStatus` untouched (out of scope).
5. Any exception opening/writing a region → return the region/error string (→ 503, retried). On full
   success return `null`; the existing handler then persists `SaasEventLog` and returns `applied`.

**Idempotency & contract:** unchanged — the existing `SaasEventLog` dedup (step 1) and post-success
insert (step 3) already wrap the switch. Your side effect must run **before** the log insert and must
**not** persist the log on failure.

## 3. Task B2 — gate toggle + live fallback

**File:** `Legeris.Office365.ServiceInterface\EntraId\GetSetupStateInternal.cs` (⚠ **dual-target**: this
file is linked into the .NET 8 `ReadAndUnderstoodAzRSvc` too — your change must compile on **both**
4.7.2 and .NET 8; use only BCL APIs present in both, and read config via the existing delegate, not
`WebConfigurationManager`).

- Read `MarketplaceStatusSource` via the existing config-reader delegate (default `"Live"` when
  unset/blank).
- Current behaviour computes `SubscribedActive` from the **live** `getSubscriptionInfo` pull. Change to:
  - `Live` (default): unchanged — live pull drives `SubscribedActive` and the `Marketplace*` state.
  - `Cached`: read the tenant's `Subscription.MarketplaceSubscriptionStatus`; `SubscribedActive =
    string.Equals(cachedStatus, "Subscribed", OrdinalIgnoreCase)`. **If the cached status is null/empty,
    fall back to the live pull** (belt-and-braces for un-provisioned/first-touch rows).
- Keep the live-pull code path intact regardless (support diagnostic + fallback). Do not delete
  `getSubscriptionInfo` wiring.

The tenant-processing gate (`Legeris.Office365.Jobs.Events\Program_TenantProcessingJob_QueueTrigger.cs`
~L107) consumes `SubscribedActive` from this resolver, so it inherits the toggle automatically — verify
no separate live-pull exists there that also needs the toggle.

## 4. Task B3 — reconcile corrector (the self-heal)

**File:** the RAU reconcile handler (`Legeris.Office365.ServiceInterface\Azure\SaaSInitialiseTenantRegionsHandler.cs`
or a sibling `SaaSInitialiseSubscriptionsHandler`). This currently reconciles only `TenantRegion` and
**ignores** subscription status.

- After the region reconcile, for each Marketplace tenant, **live-pull** `GetSubscriptionInfo`
  (`MarketplaceFulfillmentClient.GetSubscriptionInfo`) and `UpdateOnly` the tenant's `Marketplace*`
  columns to live truth — **except** rows whose `MarketplaceLastRefreshedUtc` is within a **grace
  window** (new AppSetting `MarketplaceReconcileGraceMinutes`, default e.g. `120`), so a recent test
  push isn't immediately overwritten.
- Set `MarketplaceLastRefreshedUtc = UtcNow` and (if present) `MarketplaceStatusSource = "reconcile"` on
  rows it refreshes.
- Normalize is unnecessary here (live pull already returns canonical Microsoft values); the accelerator
  reconcile snapshot is also normalized on its side.
- This makes lost/wrong/stale pushes self-heal within a day, which is the safety precondition for B2.

## 5. Task B4 — tests (NUnit, `Legeris.Office365.Tests`)

Mirror the existing static-delegate stubbing pattern in `UnitTest1-TenantSetupResolver.cs` (save/restore
of static hooks, Moq for collaborators). Extract the B1 write logic and B3 refresh logic into
**delegate-injected static helpers** so they're testable without ServiceStack ambient state. Add:
- `VerifyHmac` round-trip: known body+secret → matching/again-mismatching signature (make `VerifyHmac`
  `internal` + `[InternalsVisibleTo("Legeris.Office365.Tests")]`).
- Apply-status helper: writes canonical status; **modifiedUtc guard skips older**; updates **all** site
  rows; unresolved tenant → error (no write).
- Gate: `Cached` returns cached-derived `SubscribedActive`; **null cached → live fallback invoked**.
- Reconcile: refreshes stale rows; **skips rows inside the grace window**.

## 6. Config to set on RAU (App Settings) after check-in

| Setting | Value | Notes |
|---|---|---|
| `SaaSAcceleratorHmacSecret` | (already set) | Must equal rau-portal's `LegerisSignalingHmacSecret`. |
| `MarketplaceStatusSource` | `Live` → later `Cached` | Deploy at `Live`; flip to `Cached` once B3 is proven. Instant rollback = `Live`. |
| `MarketplaceReconcileGraceMinutes` | `120` | Grace so a fresh push isn't clobbered by reconcile. |

## 7. Rollout & verification

1. Build (MSBuild, the "Release …" config you deploy) + run NUnit. Check in via TFVC.
2. Deploy RAU with `MarketplaceStatusSource=Live` — banner/cache go push-authoritative and the corrector
   runs, but access is untouched. Verify a replayed `Unsubscribed` flips the **SiteAdminPanel banner**
   while the live-pull support view still shows real `Subscribed` (intended, visible divergence).
3. Flip `MarketplaceStatusSource=Cached` → replayed `Unsubscribed` now also **gates** the tenant. Verify
   the reconcile corrector resets it after the grace window.

## 8. End-to-end test loop (with the accelerator side)

Operator replays via rau-admin `/WebhookCapture/Index` (or the Postman lifecycle) → rau-portal updates
its DB + enqueues the outbox event → drain delivers it here → this handler writes the pushed status →
RAU reacts. No Azure billing interaction. Re-replay (fresh OperationId) to re-test.

---

### Cross-repo pointers (accelerator side, for reference)
- Producer: `src/Services/Services/SubscriptionSignalService.cs`; normalizer:
  `src/Services/Utilities/SubscriptionStatusNormalizer.cs`; snapshot:
  `src/AdminSite/Controllers/ReconcileController.cs`.
- Architecture: `docs/Outbox-Signaling-Architecture.md` (§5 event table, §6 wire protocol, §11 reconcile).
