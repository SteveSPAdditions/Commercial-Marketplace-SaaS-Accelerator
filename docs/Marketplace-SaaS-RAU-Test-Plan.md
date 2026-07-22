# Marketplace SaaS ↔ RAU — Test Plan

Covers the accelerator changes (this repo, deployed/deployable) and the RAU/Legeris changes specified
in Phase 3-C/3-D. Written **behavior-first** so it stands as acceptance criteria while 3-C/3-D are
being implemented. **Aligned to the specs as of 2026-07-22.**

> **⚠ Reconcile markers.** Items tagged **⚠** depend on final implementation detail (config-key names,
> endpoint shape, email/log strings) still settling in the RAU code. Confirm against final code before
> running. The 3-C **STATUS** section (redesign) supersedes the older §3/§4 body of that spec — the
> G-suite below follows the redesign.

## 0. How to use

- **Unit** = MSTest (`Services.Test`, accelerator) / NUnit (`Legeris.Office365.Tests`, RAU).
- **Integration** = the Postman webhook simulator in `tools/postman/` (collection + Lifecycle folder).
- **E2E** = manual, from the **E5 dev tenant** against the `rau-*` resources.
- **Observability** = App Insights (KQL) + DB inspection (`NotificationOutbox`, RAU `SaasEventLog`,
  tenant DB `Subscription`, master `TenantRegion`).
- Each case: **Pre → Do → Expect → Verify-via**. Run accelerator suites under **`Live`** (current mode)
  first, then the gate-sensitive ones under **`Cached`** after Phase 3-D + its flip runbook.

## 1. Environment & prerequisites

- E5 dev tenant; `rau` prefix resources in RG `rau-saas-commerial-marketplace-accelerator-dev`.
- **Current mode: `MarketplaceStatusSource = "Live"`** — verified on disk in RAU `Web.config` 2026-07-22.
- **Hard ordering prerequisite:** the Phase 3-C purge job must not be enabled until the Phase 3-D
  `"Activated"` receiver is deployed to **every** region (the tenant-keyed `Activated` push is what keeps
  Stage A from nominating an active resubscriber — see §5 E2E-2 / §3 G4).
- **Cached-flip runbook rules (from Phase 3-D):** the flip is a `web.config` **FILE edit** (a portal App
  Service *Application Setting* is injected as an env var and silently ignored by
  `OpenMappedExeConfiguration`). On both flip **and** rollback, **restart the WebJob in the target region
  and confirm it logs the new `MarketplaceStatusSource` at startup** (the WebJob snapshots config once at
  process start).
- Config toggles to record per run: `MarketplaceStatusSource` (`Live`/`Cached`); `AcceptSubscriptionUpdates`
  (accelerator; gates ChangePlan, not quantity/unsubscribe); the 3-C **notification** job toggles
  (`Enabled`, `DryRun`, grace days, batch cap) — **⚠ final names**.
- Partner Center webhook URL = `https://rau-webhook-buffer.azurewebsites.net/api/marketplace-webhook`.
- A way to age `MarketplaceStateAsOfUtc` back past the grace window without waiting real days. **⚠**

---

## 2. Accelerator suites (this repo — deployable now, inert-safe under `Live`)

### TS-A — Quantity change is always rejected
| # | Pre | Do | Expect | Verify-via |
|---|---|---|---|---|
| A1 | Active sub | ChangeQuantity webhook | Operation PATCHed **Failure**; Azure reverts; **DB quantity unchanged**; audit row old==new | App Insights "Quantity Change Request Rejected: this offer is single-quantity by design."; `Subscription.Quantity` unchanged |
| A2 | Active sub | ChangeQuantity new==current | Still rejected (Failure PATCH); no DB change | same |
| A3 | `AcceptSubscriptionUpdates=true` | ChangeQuantity | Still rejected (gate bypassed) | unconditional reject |
| A4 | Fulfillment PATCH mocked to fail | ChangeQuantity | 500 → Microsoft retries; `Stage=FulfillmentApi` logged | App Insights |

### TS-B — Unsubscribe provisioning-log fix
| # | Pre | Do | Expect | Verify-via |
|---|---|---|---|---|
| B1 | Sub in `PendingUnsubscribe` | Portal unsubscribe to success | Provisioning log reads **"Unsubscribed Successfully" / Unsubscribed** (not "Unsubscribe Failed") | provisioning log |

### TS-C — Activation & portal-unsubscribe signals (with unique-key hardening `1f06354`)
| # | Pre | Do | Expect | Verify-via |
|---|---|---|---|---|
| C1 | New purchase, PendingActivation | Activate (landing auto-activate) | `NotificationOutbox` row `EventType="Activated"`, payload `subscriptionStatus="Subscribed"`, `assignedTenantId=PurchaserTenantId` | Outbox; "Enqueued subscription signal Activated" |
| C2 | C1 delivered, `Live`, no 3-D yet | — | RAU **202-drops** `Activated` (unknown type); delivered row **retained** (`DeliveredUtc` set) | Buffer/RAU App Insights; outbox row present, delivered |
| C3 | AdminSite-driven activation | Activate from AdminSite | Same `"Activated"` emission | Outbox |
| C4 | Activation FAILS | — | **No** `"Activated"` signal | Outbox has none |
| C5 | Active sub | Portal unsubscribe (Fulfillment DELETE) | `NotificationOutbox` `EventType="Unsubscribed"`; RAU applies (existing case) | Outbox; SaasEventLog; tenant DB cache |
| C6 | **Blocker-1 hardening:** an `Activated` for sub X already delivered | Force a 2nd `Activated` for the **same** sub X (e.g. re-run activation path in a harness) | 2nd signal **still enqueues** — the retained delivered row does **not** suppress it (unique key per occurrence, `1f06354`) | Outbox has a 2nd row with a distinct idempotency key |
| C7 | `PurchaserTenantId` present (real purchase) | Activate | Push carries non-empty `assignedTenantId`; a `Guid.Empty` tenant id would log a warning at the receiver | Outbox payload; receiver warning absent |

### TS-D — Full-fidelity AI logging + keep-alive filter
| # | Pre | Do | Expect | Verify-via |
|---|---|---|---|---|
| D1 | Deployed | Generate traffic | Unsampled: `requests \| summarize by itemCount` → all `1` | KQL |
| D2 | Always-On on | Wait for pings | `AlwaysOn`-UA requests **absent** from `requests`; real users present | KQL |
| D3 | WebhookBuffer | Trigger function | Sampling off; exceptions still captured | Buffer App Insights |

### TS-E — Existing webhook signals (regression)
Run the Postman Lifecycle folder: **ChangePlan, Suspend, Reinstate, Unsubscribe**. Each still updates the
AMP DB and enqueues its signal with the **real Marketplace `operationId`** (so distinct operations get
distinct keys — a `Suspend → Reinstate → Suspend` cycle enqueues three separate signals, none deduped).
Verify outbox + RAU `SaasEventLog`.

---

## 3. RAU suite — Phase 3-C (7-day unsubscribe deprovisioning) **⚠ against final code; job is NON-DESTRUCTIVE**

The redesigned job **detects and notifies**; it does **not** drop anything itself. It emails
`team@spadditions.com` a link to an **operator-initiated endpoint** that performs the deprovisioning after
re-checking. Prerequisite: **Phase 3-D `"Activated"` receiver deployed to all regions first.**

| # | Pre | Do | Expect | Verify-via |
|---|---|---|---|---|
| G1 | Tenant cached `Unsubscribed`, `MarketplaceStateAsOfUtc` ≥ 7d old | Run detection (`Enabled=true`, `DryRun=false`) | Candidate nominated → **ops email to team@** with a link that **omits `code=`**; **nothing deleted** | mailbox; no DB changes |
| G2 | Same but 6 days old | Run detection | **Not** nominated | — |
| G3 | `MarketplaceStateAsOfUtc` null | Run detection | **Not** nominated (Stage A now ages correctly via `NextStateAsOfUtc`; a baseline is stamped, not `now` every pass) | — |
| G4 | **Resubscribe-in-window (primary guard):** tenant unsubscribed, then resubscribed; 3-D receiver deployed | Wait for the `Activated` push; run detection | `Activated` flipped the tenant-DB cache to `Subscribed` **by tenant** → Stage A **does not nominate** → no email | tenant DB cache `Subscribed`; no ops email |
| G5 | **Interlock:** a nominated candidate | Inspect the ops email + endpoint | `code=` value **= `TenantRegions.SubscriptionId`**, is **absent from the email**, and the endpoint **rejects** a missing/wrong code; code is a **second factor, not auth** (endpoint still requires real auth) | email body; endpoint responses |
| G6 | **Stage B at click time:** operator clicks a days-old link | Endpoint re-runs the live Fulfillment check | `GetSubscription` → **`Found`+`Subscribed`** ⇒ abort (resubscribed); **`NotFound` (explicit 404)** ⇒ proceed; **`Indeterminate`** (403/5xx/timeout/missing config) ⇒ **abort, never purge** | endpoint outcome; logs |
| G7 | **Revoke branches:** consent/app removed (`AADSTS700016`) vs transient Graph error | Trigger deprovision | consent-gone ⇒ **nothing to revoke → success, proceed**; transient ⇒ **abort + retry** (does NOT dead-letter forever) | logs; tenant state |
| G8 | Confirmed purge | Endpoint deprovisions | Reuses the `Migrate.cs` path: `MdbTenant` deleted (cascades `SiteCollections`), `ZoHoSubscriptions` cleared, tenant DB dropped; `TenantRegion` removed per the replicated-row decision (`Migrate.cs:104` precedent) | master + tenant DB across regions |
| G9 | Deprovision succeeded | — | **Customer email** sent **by the endpoint AFTER** success (never before), to `TenantSiteCollections.BackupEmailAddress` where `BackupEmailAddressValidated != null` — captured **before** the DB drop | mailbox; capture-before-drop |
| G10 | `Enabled=false` OR `DryRun=true` | Run | **No action**; requires **both** `Enabled=true` **and** `DryRun=false` to act; mode logged at startup | logs only |
| G11 | Same candidate on consecutive days | Run detection twice | Ops notification **deduped** (marker in master, not tenant DB) — team@ not emailed daily per tenant | one email |
| G12 | Wrong purge hypothetical | — | **Azure SQL PITR** is the documented recovery path (numbers recorded per region); the audit record alone is not recovery | spec §; PITR settings |
| G13 | Dev (LH/DEV) run vs prod-region tenant | Run | Never crosses the isolation boundary | scope check |

---

## 4. RAU suite — Phase 3-D (`"Activated"` receiver + Cached mode) **⚠ against final code**

`"Activated"` dispatch is **✅ DONE on the RAU side** (aliased onto the `Reinstated` apply path;
`IsPushedStatusEventType` set drives dispatch; 28/28 NUnit pass) — **not yet in TFVC**.

| # | Pre | Do | Expect | Verify-via |
|---|---|---|---|---|
| H1 | `"Activated"` push, cached `Unsubscribed` older `StateAsOfUtc` | Deliver | Cache → `Subscribed`; F2 guard applied | tenant DB; SaasEventLog |
| H2 | `"Activated"` push, cached newer `StateAsOfUtc` | Deliver | **Skipped** (guard); 200 not 503 | receiver |
| H3 | `"Activated"` null/blank status | Deliver | 400 (F5) | receiver |
| H4 | Dispatch coverage | Deliver `Activated` (mixed case) | Routes to status path via `IsPushedStatusEventType`, **not** unhandled/default | dispatch test |
| I1 | `Cached`, cached `Subscribed` | GetSetupState | `SubscribedActive=true` (gate open) | setup-state |
| I2 | `Cached`, cached `Unsubscribed` | GetSetupState | `SubscribedActive=false` (blocked) | setup-state |
| I3 | `Cached`, cached null | GetSetupState | Falls back to **live** pull | setup-state |
| I4 | `Cached`, resubscribe whose `Activated` landed | Load app | Gate opens **without** a reconcile | E2E-2 |

---

## 5. End-to-end scenarios (both codebases, E5 tenant)

- **E2E-1 New purchase → activate → use.** Purchase → auto-activate → `Activated` → (post-3-D) cache
  `Subscribed` → AppAddin2 gate opens → tenant DB provisioned → usable.
- **E2E-2 Resubscribe within grace.** Unsubscribe, then re-purchase (**new AMP sub id**) inside 7d.
  Expect: **same tenant DB reused** (data continuity); the `Activated` push updates the cache **by tenant**
  (independent of the stale `TenantRegions.SubscriptionId`); under `Live` not blocked, under `Cached` the
  gate opens once `Activated` lands. Stage A never nominates them for purge (G4).
- **E2E-3 Unsubscribe → deprovision (non-destructive flow).** Unsubscribe, age 7d → **ops email to team@**
  → operator looks up the `code=` (`TenantRegions.SubscriptionId`, not in the email) → endpoint re-checks
  live status → deprovisions → **customer email** to the validated `BackupEmailAddress`. Resubscribe after
  that provisions fresh.
- **E2E-4 Change plan** round-trip (trial→live); signal + RAU plan update.
- **E2E-5 Suspend → Reinstate → Suspend** — three distinct operations, three distinct outbox signals
  (real `operationId`s), none deduped; cache tracks `Suspended`→`Subscribed`→`Suspended`.
- **E2E-6 Quantity change rejected** — Azure shows the operation failed/reverted.
- **E2E-7 Delete of an already-unsubscribed sub** (negative) — deleting the tombstone tile fires **no
  webhook**; buffer/Service Bus silent; **correct**, not a lost message.
- **E2E-8 Cached flip + rollback.** After 3-D deployed to all regions + reconcile true-up: **edit
  `web.config` (FILE, not an App Setting)** to `Cached` in one region; **restart the WebJob and confirm the
  startup log** shows `Cached`; verify gates still open for active tenants; then roll back to `Live` — edit
  the file **and restart the WebJob again**, confirming the startup log shows `Live`.

---

## 6. Observability cheat-sheet

- **Unsampled:** `requests | where timestamp>ago(1h) | summarize by itemCount` → expect `1`.
- **Keep-alive filtered:** `requests` filtered on `AlwaysOn` UA → expect empty.
- **Signal emitted:** `traces | where message contains "Enqueued subscription signal"`.
- **Outbox:** `SELECT * FROM NotificationOutbox` — delivered rows are **retained** (`DeliveredUtc` set),
  not deleted; a pending (`DeliveredUtc IS NULL`, not dead-lettered) row means undelivered.
- **RAU applied:** `SaasEventLog` row for the key; tenant DB `Subscription.MarketplaceSubscriptionStatus`
  + `MarketplaceStateAsOfUtc`.
- **WebJob mode (Cached rollout):** confirm the WebJob's **startup** log line shows the current
  `MarketplaceStatusSource` after any flip/rollback.

## 7. Traceability

| Area | Change / spec | Commit(s) |
|---|---|---|
| TS-A | Quantity reject | 378ed20 |
| TS-B | Unsubscribe log fix | 378ed20 |
| TS-C (C1–C5,C7) | Activation + portal-unsubscribe signals | 2632b83 |
| TS-C (C6) | Unique-key idempotency hardening (Blocker 1) | 1f06354 |
| TS-D | Full-fidelity AI logging + keep-alive filter | ecdbbb9 |
| TS-E | Existing signals (Phase 2/3-A) | 808240c, cd7b8ad, 06f4fe8 |
| TS-G | Phase 3-C deprovisioning (non-destructive) | docs/Phase3C-… (RAU, in progress) |
| TS-H/I | Phase 3-D Activated + Cached | docs/Phase3D-… (RAU: receiver done, not in TFVC) |

## 8. Open items to reconcile once 3-C/3-D land (⚠)

- Final **notification** config-key names + the grace-days/batch-cap knobs (3-C keys now govern
  notification, not deletion).
- The operator endpoint's exact shape: route, auth, the `code=` interlock check, and the click-time Stage B
  re-check wording.
- Customer + ops email templates; the ops-notification dedup marker location.
- `TenantRegion` replicated-delete decision (delete from all masters vs the `Migrate.cs:104` commented-out
  precedent) and confirmation the `Migrate.cs` purge path is reused.
- Azure SQL **PITR retention** numbers per region (the real recovery path).
- `PurchaserTenantId` always populated at activation (drives the tenant-keyed guard; C7).

### Resolved since first draft (no longer open)
- **Current mode confirmed `Live`** (was ambiguous) — verified on disk 2026-07-22.
- **Blocker 1 fixed** (`1f06354`) — unique idempotency key for `Guid.Empty` paths; the `Suspend →
  Reinstate → Suspend` concern was a false alarm (webhook paths use real `operationId`s).
- **Ordering constraint locked:** 3-D `Activated` receiver before the 3-C job (both specs).
