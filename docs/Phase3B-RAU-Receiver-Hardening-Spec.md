# Phase 3-B — RAU receiver hardening (spec for Claude-in-VS)

> **Audience:** Claude running in Visual Studio with the **Legeris ("Read and Understood")** solution open
> (`D:\VSTFSWork\Legeris for SharePoint`). This repo is **TFVC** (not git), **.NET Framework 4.7.2**,
> **ServiceStack + OrmLite + Newtonsoft**, built with **MSBuild/VS** (not `dotnet`). **Read the repo's
> root `CLAUDE.md` first.** Build with MSBuild; check in with TFVC.
>
> **Companion to** [Phase3-RAU-Push-Authoritative-Spec.md](Phase3-RAU-Push-Authoritative-Spec.md).
> That spec defined the feature; this one fixes defects found in the delivered implementation.
> Do not re-litigate the locked decisions in section 0 of that document.

## 0. Context

Task B1 (receiver cases + `ApplyPushedSubscriptionStatusAsync`) is **implemented and correct in its core
intent**. The ghost-FarmId reverse map (`.DbSideTenantId()`) and the no-silent-noop rule
(`ClassifyPushOutcome`) both landed as specified, and `ClassifyPushOutcome`'s decision to *not* 503 when
rows matched but were all skipped by the ordering guard is right — 503-ing there would make every
duplicate delivery retry forever.

A full read-only review then found defects, all verified against both sides of the wire. They are ordered
below by severity. **F1–F4 are the ones that lose or misroute data; do those first.**

**The retry contract you are designing against** (`src/Services/Services/LegerisSignalingDispatcher.cs`
in the accelerator repo, verified):

| Receiver returns | Sender does |
|---|---|
| 2xx | `Delivered` — clears the outbox row, **never retries** |
| 408, 429, 5xx | `Transient` — retries on backoff (30s…24h, 12 attempts, ~52h) then dead-letters |
| any other 4xx | **`Permanent` — dead-letters immediately, zero retries** |

Two rules follow, and most findings below are a violation of one of them:

1. **Never return 2xx for work that did not happen.** The sender deletes the row; the event is gone.
2. **Never return a non-408/429 4xx for a condition that could clear on its own.** That is a
   zero-retry dead-letter requiring manual replay.

---

## F1 — Unconfigured receiver secret returns 401, which dead-letters on attempt one

**Severity: high.** `SaasAcceleratorEventHandler.cs:52-57`, `VerifyHmacWithSecret:~582`.

`VerifyHmacWithSecret` returns `false` when RAU's own `SaaSAcceleratorHmacSecret` AppSetting is empty or
missing. That is a **receiver-side misconfiguration**, not a bad request — but the handler answers 401,
which the sender maps to `Permanent`.

**Failure scenario.** Secret rotation: the accelerator's `LegerisSignalingHmacSecret` is updated at T, and
RAU's `web.config` a few minutes later at T+3. Every event dispatched in that window returns 401 and
**dead-letters on its first attempt**. Nothing retries when the config catches up; each row needs manual
replay. Same outcome for a `web.config` reload that briefly drops the setting.

**Fix.** Separate the two conditions in the handler:

- Secret **missing/empty** (receiver not configured) → log `Critical`, return **503**.
- Secret present and the signature **does not match** → keep the current 401.

Restructure `VerifyHmac` to distinguish them rather than returning a bare `bool` — e.g. return an enum
`{ Ok, Mismatch, NotConfigured }`, or have the caller read the AppSetting and check for emptiness before
calling. Keep the constant-time comparison exactly as it is.

**Test.** Extend `Legeris.Office365.Tests/UnitTest1-Phase3RauPush.cs`: a valid body with the secret
unset must classify as `NotConfigured` (→503), and a valid body with a wrong signature as `Mismatch`
(→401).

---

## F2 — `MarketplaceLastRefreshedUtc` carries two different clocks, so valid pushes are silently dropped

**Severity: high — this one loses data while reporting success.** Verified: three writers, two meanings.

| Site | Writes | Meaning |
|---|---|---|
| `SaasAcceleratorEventHandler.cs:499` | `modifiedUtc` | **when the change occurred** (event time) |
| `SaaSInitialiseSubscriptionsHandler.cs:182` | `now` (`DateTime.UtcNow`) | when the cache was refreshed |
| `InitialiseSaasTenant.cs:263` | `DateTime.UtcNow` | when the row was provisioned |

The ordering guard at `:493` (`row.MarketplaceLastRefreshedUtc >= modifiedUtc → skip`) compares against
whichever of the two got written last. Server-now is always ≥ any in-flight event's `modifiedUtc`, so a
reconcile pass or a provisioning write makes the guard treat a **pending, not-yet-applied** push as
already superseded.

**Failure scenario** (on the expected path, because F3's 503-until-provisioned behaviour guarantees the
crossing):

1. T+0 — `TenantRegionFanOut` for a new tenant.
2. T+2min — `PlanChanged` enqueued with `modifiedUtc = T+2min`. It 503s repeatedly because the tenant DB
   isn't provisioned yet (`:421-433`).
3. T+5min — provisioning completes, inserting the `Subscriptions` row with
   `MarketplaceLastRefreshedUtc = T+5min` (`InitialiseSaasTenant.cs:263`).
4. T+6min — the outbox retries. The row now exists, so `rowCount = 1`. The guard sees
   `T+5min >= T+2min` and skips. `updated = 0`, `rowCount > 0` → `ClassifyPushOutcome` returns null →
   **HTTP 200 "applied"** → sender marks Delivered and deletes the row.

The plan change is lost until the next daily reconcile. The same shape occurs any time an outbox retry
crosses a reconcile run — and since backoff reaches 24h while reconcile is daily, that is not rare.

**Fix — separate the two concepts into two columns.** They are genuinely different facts and one column
cannot serve both guards.

- Add `MarketplaceStateAsOfUtc` (`DateTime?`) to `Legeris.Office365.ServiceModel/dbModel/365Subscriptions.cs`,
  alongside the existing `MarketplaceLastRefreshedUtc`. Follow whatever schema-upgrade mechanism the
  tenant DB uses for added columns (`GetOrmLiteConnection`'s non-`skipUpgrade` path).
- **`MarketplaceStateAsOfUtc` = when the state was true at the source.** Push writes `modifiedUtc`;
  reconcile writes the live pull's own timestamp if the Fulfillment payload carries one, otherwise `now`.
  **The ordering guard reads only this column.**
- **`MarketplaceLastRefreshedUtc` = when we last wrote the cache.** All three writers set it to
  `DateTime.UtcNow`. **The reconcile grace window (`ShouldReconcileRefresh`) reads only this column** —
  that is already its correct meaning there, so `SaaSInitialiseSubscriptionsHandler.cs:171` needs no change.
- Backfill: treat a null `MarketplaceStateAsOfUtc` as "no known state time" so the guard applies the push
  (matching today's null handling at `:493`).

**Test.** Extend `UnitTest1-Phase3RauPush.cs`, which already covers the older/newer/equal guard cases:
a row whose `MarketplaceLastRefreshedUtc` is *newer* than `modifiedUtc` but whose `MarketplaceStateAsOfUtc`
is *older* must **apply**. That is the regression this fix exists to prevent.

---

## F3 — Cross-region resolve ignores the LH/DEV isolation rule, so dev can write production rows

**Severity: high.** `SaasAcceleratorEventHandler.cs:537-545` vs `:240-244`.

`FanOutSaasTenantRegionAsync` filters candidate regions through
`LhDevRegions.Contains(region) == isThisRegionLhOrDev` — the rule that keeps dev and prod instances from
touching each other's masters. `ResolveTenantBySubscriptionId` iterates the **same `azRegions` list with no
such filter**.

The trigger is real and already in the sender: `SubscriptionSignalService.cs:66` (accelerator repo) emits
`assignedTenantId = subscription.PurchaserTenantId ?? Guid.Empty`, and `Guid.Empty` is exactly what routes
the handler down the resolve-by-subscription path at `:374-379`.

**Failure scenario.** A DEV push arrives with `assignedTenantId = Guid.Empty`. The dev master has no
matching `TenantRegion`. The loop continues into `MasterDbUSA`, finds the **production** row for that
subscription id, and the handler proceeds to open the production tenant DB (`:411-419`) and write
`Marketplace*` columns into production rows. The ghost-id split at `:406` does **not** protect you — in
production the mapping is identity pass-through, so `dbSideTenantGuid` resolves correctly against the prod
master.

**Fix.** Apply the identical LH/DEV filter in `ResolveTenantBySubscriptionId`. Extract the predicate used
at `:242-243` into a single private helper (e.g. `IsRegionInThisIsolationScope(string region)`) and call it
from **both** sites, so the two can't drift again.

---

## F4 — Zero configured regions is reported as complete success

**Severity: high.** `SaasAcceleratorEventHandler.cs:247-250`.

When `azRegions` is unset, or every entry is filtered out by the LH/DEV rule, `tasks.Length == 0` and the
method returns an **empty failure list** — indistinguishable from "all regions succeeded". The handler then
persists `SaasEventLog` and returns 200. No `TenantRegion` row is written anywhere, and the sender clears
the outbox. This is the same zero-work-returns-applied class of bug that `ClassifyPushOutcome` was written
to eliminate, surviving in the one path that never got the treatment.

Two configuration defects feed it, both verified:

- **`LhDevRegions` is a `const string = "LH,DEV"` (line 34), so `.Contains()` is substring matching.**
  `"LH,DEV".Contains("")` is **`true`**, so a production instance with `azRegion` unset classifies *itself*
  as LH/DEV and fans out to the wrong set — or to nothing, hitting the case above.
- **Line 238 does not `.Trim()` the CSV split.** `azRegions = "LH, DEV"` yields `" DEV"`, which fails the
  substring test and then produces a null connection string at `:267` — a permanent misconfiguration that
  presents as a transient region failure and loops until dead-letter.

**Fix.**

1. In `FanOutSaasTenantRegionAsync`, when `tasks.Length == 0`, log `Critical` and return a sentinel the
   caller surfaces as **503** — never an empty (success) list.
2. Change `LhDevRegions` to a `static readonly string[] { "LH", "DEV" }` and match with
   `Array.IndexOf`/`Contains` on exact, case-insensitive equality. This kills the empty-string match and
   the accidental single-letter matches.
3. `.Trim()` each entry of both `azRegions` splits (lines 238 and 537-538).
4. Treat an unset `azRegion` as a hard configuration error — log `Critical` and 503 rather than silently
   inferring an isolation scope from an empty string.

---

## F5 — A null `subscriptionStatus` blanks the cached status and reports success

**Severity: medium-high.** `SaasAcceleratorEventHandler.cs:168-172` and `:496-497`.

`planId` is guarded (`if (!string.IsNullOrEmpty(planId))` at `:497`); **`status` is not**.
`body.SubscriptionStatus` is never validated in the dispatch block. Note that the check actually present at
`:168` (`saasSubscriptionId == null`) is **dead code** — that was already rejected at `:83` — which is what
makes the missing status check easy to miss on a read-through.

**Failure scenario.** A replayed capture or hand-built payload omits `subscriptionStatus` →
`row.MarketplaceSubscriptionStatus = null` on every site row, `MarketplaceLastRefreshedUtc` bumped, HTTP
200 "applied". RAU's cached status is now blank, and the ordering guard will reject any older corrective
event. With `MarketplaceStatusSource = Cached`, the gate then falls back to the live pull on null — so this
degrades quietly rather than breaking loudly, which is worse for diagnosis.

**Fix.** In the four status cases, reject a null/whitespace `subscriptionStatus` with **400** — matching
how `TenantRegionFanOut` rejects a missing region at `:137`. 400 is correct here: a payload missing a
required field will never become valid on retry, so dead-lettering it is the right outcome. Delete the dead
check at `:168` while you are there.

---

## F6 — Unhandled event types are permanently recorded as applied

**Severity: medium.** `SaasAcceleratorEventHandler.cs:186-191, 194-208`.

The `default` branch sets `unhandled = true` but still falls through to the Step 3 `SaasEventLog` insert
and returns 202. The sender treats 2xx as `Delivered`.

**Failure scenario.** The accelerator starts emitting a new type (e.g. `QuantityChanged`) before RAU
implements it. Every one is 202'd and logged. RAU ships the handler a week later; an operator replays the
captured events — and **every one hits the dedup short-circuit at `:116-126`** and returns "already-applied"
without ever running the new handler. The events are unrecoverable without hand-deleting `SaasEventLog` rows.

**Fix.** Either (a) do **not** persist `SaasEventLog` on the unhandled path — return 202 without logging, so
a later replay runs the new handler; or (b) add a `HandlerVersion` column and key dedup on
`(IdempotencyKey, HandlerVersion)`. **(a) is preferred** — it is a two-line change and matches the existing
"don't persist on paths we want retried" convention already used at `:147-149`.

---

## F7 — `ZoHoSubscriptions` pending-row insert may collide on the primary key

**Severity: medium, but verify the live schema before changing code.**
`SaasAcceleratorEventHandler.cs:322-333` with `Legeris.Office365.ServiceModel/dbModel/ZoHoSubscription.cs:25-27`.

**As modelled**, `ZoHoSubscription` has `[PrimaryKey]` on **`SiteId` alone** (`Id`/`TenantId` is only
`[Required]`), while every pending SaaS row is written with `SiteId = Guid.Empty`. The existence probe
(`z.Id == tenantId && z.SiteId == Guid.Empty`) is tenant-scoped; the PK is not.

**Failure scenario.** Tenant A fans out into region USA leaving `(TenantId=A, SiteId=Empty)`. A's DB
creation stalls, so cleanup hasn't removed it. Tenant B fans out into USA: the probe on `Id == B` returns
null, `Insert` runs with `SiteId = Guid.Empty` → PK violation → caught at `:337` → region reported failed →
503 → retried → same violation every time → dead-letters after ~52h. B never onboards in that region. The
`TenantRegion` upsert already succeeded before the throw, so the retries are wasted and a permanent failure
is masquerading as transient.

**Fix.** **First confirm the deployed table's actual PK** — if `ZoHoSubscriptions` was created with a
composite `(SiteId, Id)` PK, this does not fire and the model attribute is merely misleading (fix the
attribute only). If the live PK really is `SiteId` alone, the pending-row pattern needs a distinct
per-tenant `SiteId` sentinel rather than `Guid.Empty`; propose the approach before implementing, since it
touches a table shared with the ZoHo path.

---

## F8 — Lower severity, batch together

| # | Site | Issue | Fix |
|---|---|---|---|
| a | `:438-447` | Ordering guard is read-then-write, not a conditional update. Two concurrent deliveries (outbox lease expiry during a slow request — which the 503-heavy paths actively encourage) can both pass the guard and let the older event land last. | Put the guard in the WHERE: `&& s.MarketplaceStateAsOfUtc < modifiedUtc` (using F2's new column). Makes it atomic. |
| b | `:298-312` | `TenantRegionFanOut` has **no ordering guard at all** — `AzureRegion` and `SubscriptionId` are overwritten unconditionally and `body.ModifiedUtc` is never compared to `existing.Modified`. Out-of-order delivery (trivial: one event in backoff while a later one delivers immediately) pins the tenant to the **older** region, and every subsequent status push then opens the wrong regional master at `:411`. | Apply the same `modifiedUtc` guard used on the status path. |
| c | `:91-95` | `X-Idempotency-Key` is read from an **unsigned header** and is the sole dedup key. The HMAC correctly covers only the body. Anyone replaying a captured request with a fresh key bypasses dedup entirely; each replay inserts a `SaasEventLog` row with `Payload = raw` (VARCHAR(MAX)) → unbounded growth. There is also no replay window — `modifiedUtc` is never compared to `DateTime.UtcNow`. | Derive the key from **signed body fields** and require the header to match the derived value if present. Consider rejecting `modifiedUtc` older than a configurable window. |
| d | `:549-561` | `TryFindTenantBySubscriptionId` swallows **every** exception with no logging. A dead connection string, firewall change, or DB outage is indistinguishable from "row absent" and yields the generic `"tenant not resolvable"` at `:377`. Classification is accidentally correct (both → 503), but a permanently broken regional connection string retries for 52h with no log line naming the region. | Log the exception per region at `Warning`. |
| e | `:150-152`, `:178-179` | Error strings interpolated into JSON **unescaped**. `applyError` at `:473` is `ex.Message`, which routinely contains double quotes for SQL errors (`Cannot open server "..."`, `Login failed for user '...'`) → malformed JSON. Cosmetic today (the sender stores it as an opaque 512-char snippet) but will break any structured parsing. | Build the response with the JSON serializer instead of string interpolation. |
| f | `:91`, `SaasEventLog.cs:20` | `IdempotencyKey` is `VARCHAR(255)` and never length-checked, though the value is externally supplied. An over-length key throws at the Step 3 insert (`:199`) **after** side effects have run → 500 → retry → dedup misses (row never written) → side effects re-run → dead-letter. Sender keys are short today. | Validate length at `:91`; reject with 400. |

---

## Verified correct — do not "fix" these

- **HMAC comparison is timing-safe** (length check then XOR-accumulate; the only leak is signature length,
  which is not secret). `sha256=` prefix handling is case-insensitive and tolerates a bare hex signature.
- **The signature covers the raw body.** `IRequiresRequestStream` correctly prevents ServiceStack
  auto-binding, and the same string is used for both verification and deserialization.
- **Check ordering is right** — HMAC before empty-body and before JSON parse, so unauthenticated input
  never reaches the deserializer. Preserve this ordering through the F1 change.
- **Idempotency after a partial success holds.** If the Step 3 insert fails after a successful apply, the
  retry's dedup misses, every row is skipped by the guard, and `ClassifyPushOutcome` correctly returns null
  rather than 503-ing forever. Fan-out likewise no-ops via the `existing != null` probes.
- **`TenantRegion.TenantId` is `[Index(true)]`** (unique), so `Single<TenantRegion>` at `:276` and `:386`
  cannot throw on duplicates.
- **Dispatch coverage matches the sender** exactly: `PlanChanged`, `Reinstated`, `Suspended`,
  `Unsubscribed`, plus `TenantRegionFanOut`. The `Suspend → Suspended` normalization is sender-side, so the
  receiver's direct string comparison is correct.

**One latent edge, no action needed now:** `new StreamReader(stream, Encoding.UTF8)` at `:44` enables BOM
detection, so a UTF-8 preamble would be stripped from `raw` before hashing and mismatch the sender.
`StringContent` emits no preamble so this cannot fire today — but if a proxy or hand-built replay tool ever
added one, the symptom is a 401 that (before F1) dead-letters instantly.

---

## Out of scope here — a sender-side gap to log separately

Quantity changes emit **no signal at all** from the accelerator, so RAU's `MarketplaceQuantity` only ever
moves on the daily reconcile. That is a gap in `SubscriptionSignalService`/`WebHookHandler` on the
accelerator side, not a receiver defect. Raise it as its own item; do not fix it in this pass.

---

## Suggested order of work

1. **F1** — smallest change, removes the manual-recovery failure mode during any secret rotation.
2. **F4** + **F3** — same region-handling code; do them together while it is paged in.
3. **F5**, **F6** — small, self-contained, each a few lines.
4. **F2** — the schema change. Largest blast radius, so land it on its own with the regression test.
5. **F8a** — folds naturally into F2 (it uses the new column).
6. **F8b–f** — batch as one cleanup pass.
7. **F7** — schema investigation first; propose before implementing.

Build with MSBuild, run `Legeris.Office365.Tests` (NUnit), and no check in via TFVC. `UnitTest1-Phase3RauPush.cs`
already covers `ApplyPushedStatusToRows` and `ClassifyPushOutcome` — extend it rather than starting a new
fixture.
