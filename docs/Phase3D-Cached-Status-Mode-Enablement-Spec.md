# Phase 3-D — Enabling `MarketplaceStatusSource = "Cached"` (spec for Claude-in-VS)

> **Audience:** Claude running in Visual Studio with the **Legeris ("Read and Understood")** solution
> open (`D:\VSTFSWork\Legeris for SharePoint`). **TFVC**, **.NET Framework 4.7.2**,
> **ServiceStack + OrmLite + Newtonsoft**, MSBuild/VS. **Read the repo's root `CLAUDE.md` first.**
> Companion to [Phase3B-RAU-Receiver-Hardening-Spec.md](Phase3B-RAU-Receiver-Hardening-Spec.md)
> and [Phase3C-Unsubscribe-Deprovisioning-Purge-Spec.md](Phase3C-Unsubscribe-Deprovisioning-Purge-Spec.md).

---

## ⚠ STATUS — 2026-07-22

### §1 receiver change — ✅ DONE (RAU side, NOT checked into TFVC)

`"Activated"` dispatch added to `SaasAcceleratorEventHandler`, aliased onto the exact push-status path
`Reinstated` uses (F2 ordering guard + F5 null-status rejection apply automatically). Both targets build
clean, 28/28 NUnit tests pass. Implementation notes:

- The switch was refactored so an `internal static bool IsPushedStatusEventType(string)` set **is** the
  dispatch decision (`{Activated, Reinstated, PlanChanged, Suspended, Unsubscribed}`), not a parallel
  list that can drift from the `case` labels. Adding a future status type is now a one-line change.
- The `default` branch comment was corrected — Phase 3-B F6 already stopped persisting `SaasEventLog`
  on the unhandled path, so `"Activated"` reached the handler correctly even before this change.
- Tests added: dispatch routes `Activated` to the status path (case-insensitive) and NOT to unhandled;
  and the resubscribe reactivation — an `Activated` (newer `modifiedUtc`) applies over a stale
  `Unsubscribed` row, while a genuinely newer transition still wins.

### ✅ Blocker 1 RESOLVED (accelerator `1f06354`) + 🟠 one runbook amendment before flipping to `"Cached"`

**Blocker 1 — RESOLVED.** *Was: the `Guid.Empty` no-op-id paths used a constant dedup key
`{eventType}|{sub:N}|000…0`; because `MarkDelivered` (`NotificationOutboxRepository.cs:65-76`) retains the
delivered row and `GetByIdempotencyKey` (`:100-104`) does not filter `DeliveredUtc`, a same-key repeat
would be suppressed.* Fixed sender-side in the accelerator — commit **`1f06354`**: `SubscriptionSignalService`
now keys `Guid.Empty` occurrences uniquely (fresh GUID); webhook paths (real `operationId`) are unchanged.
**`git pull` the accelerator main to pick it up.** This no longer gates the flip.

**Correction to the earlier scope note:** the `Suspend → Reinstate → Suspend` example was **wrong**.
Webhook Suspend/Reinstate pass the **real Marketplace `payload.OperationId`** (`WebhookHandler.cs:335` and
`:283`), not `Guid.Empty` — three distinct operations give three distinct keys, so no false dedup ever
occurred. The only `Guid.Empty` paths are `"Activated"` and portal-`"Unsubscribed"`, and both are
**terminal-once per sub id** (a subscription activates once, unsubscribes once; resubscribe = a *new* id),
so no same-key repeat arose in practice either. The `1f06354` fix is belt-and-braces hardening, not a fix
for an active defect.

**🟠 Runbook amendment (was "Blocker 2") — §3's "rollback is instant, no redeploy" is right for the Web
app but incomplete for the WebJob.** *Downgraded after tracing the code — this is a runbook gap, not a
code defect, and not a hard blocker.*

Code fact (certain): **both** hosts read the toggle from a one-time **file snapshot** —
`OpenMappedExeConfiguration(web.config)` at `AppHost.cs:134` (Web app) and `Program.cs:80` (WebJob).
Neither re-reads on access.
- **Web app / AppAddin2:** editing `web.config` recycles w3wp → `AppHost` re-runs → re-snapshots, so their
  gate flips **immediately**. §3's "instant, no redeploy" is correct for them. No redeploy anywhere — a
  file edit, not a redeploy.
- **WebJob** (the tenant-processing gate — the consumer that actually blocks customers): `Main` runs once
  then `host.RunAsync` (`Program.cs:418`) blocks for the process lifetime, so `_configuration` is frozen
  at **WebJob-process-start**. It only changes when the **WebJob process restarts**. In prod a `web.config`
  edit restarts the App Service and continuous WebJobs restart with it, so it *usually* propagates — but
  via a restart (startup lag + a brief gap in processing), not a hot-swap, and not something to bet an
  emergency rollback on unverified. In **dev** (console-hosted WebJob) there is no auto-restart — restart
  it by hand (as seen 2026-07-20).

**Two concrete runbook items:**
1. §3 steps 4 AND 5: add **"restart the WebJob in the target region and confirm it logged the new
   `MarketplaceStatusSource` at startup"** — to both the flip and the rollback. Cheap insurance; removes
   the ambiguity during an incident, when you don't want to be guessing whether a continuous WebJob
   recycled.
2. **The flip MUST be a `web.config` FILE edit.** Because both hosts read the physical file via
   `OpenMappedExeConfiguration`, a portal **App Service Application Setting** named `MarketplaceStatusSource`
   is injected as an env var and **silently ignored** by this code path — the toggle would appear to do
   nothing. (Applies to the Web app too, not just the WebJob.)

One-time: **verify** whether a `web.config` edit reliably restarts your continuous WebJobs in prod. If
yes, item 1 is belt-and-braces; if no, it is mandatory.

### ✅ ANSWERED (accelerator trace, 2026-07-22): a resubscribe gets a NEW AMP subscription id

Confirmed in the accelerator (`SubscriptionsRepository.Save` keys on `AmpsubscriptionId`; Marketplace
issues a new subscription GUID per purchase). See the matching trace in the Phase 3-C spec. Two
consequences:

- **This re-scopes Blocker 1** (now RESOLVED — see above). A resubscribe's `"Activated"` carries the
  **new** sub id, so its key differs and it fires normally. The `Guid.Empty` dedup never touched webhook
  Suspend/Reinstate (those carry real, distinct `payload.OperationId`s), and the only `Guid.Empty` paths
  (`Activated`, portal-`Unsubscribed`) are terminal-once per sub id — so no active defect existed. The
  accelerator fix (`1f06354`) hardens it regardless.
- **The Phase 3-C purge false-positive is closed by deploying this receiver first.** Because `"Activated"`
  updates the cache **by tenant** (`FarmId`), independent of the stale `TenantRegions.SubscriptionId`, a
  resubscriber's cache flips to `Subscribed` → 3-C Stage A never nominates them → the stale-id Stage B
  live-check never runs for them. **Hard ordering: deploy this 3-D receiver to every region BEFORE
  enabling the 3-C purge job.** The dangerous window is exactly "3-D receiver not deployed **and** fan-out
  not re-landed" — see the 3-C trace.

### Endorsed as-is

§1's aliasing approach; §2's tenant-resolution and F2-guard reasoning; §3 step 3's reconcile true-up
(works with the 3-C `NextStateAsOfUtc` change — reconcile still writes `MarketplaceSubscriptionStatus`
every pass; only `StateAsOfUtc` is now change-gated); §3's per-region, one-region-first rollout.

### 🔗 Downstream dependency — the 3-C purge job requires this receiver first

Accelerator-side trace (2026-07-22): the `"Activated"` push updates the RAU cache **by tenant, not
subscription id** (`SaasAcceleratorEventHandler.cs:599` selects by `FarmId`), so it is what keeps an
active resubscriber's cached status = `Subscribed` — which is what stops Phase 3-C **Stage A** from ever
nominating them for purge. 3-C's Stage B (`TenantRegions.SubscriptionId` live-check) is only a secondary
guard and can legitimately be stale on resubscribe (the fan-out re-anchors that column only if the
customer re-selects region, but the `TenantRegion` row persists by tenant so it may not).

**Hard ordering constraint (also recorded in 3-C): the Phase 3-C purge job must NOT be enabled until this
`"Activated"` receiver is deployed to every region.** Until then, `Activated` is 202-dropped, the cache
stays stale `Unsubscribed`, and a resubscriber with a stale `TenantRegions.SubscriptionId` could be
falsely purged. This receiver is therefore a prerequisite for BOTH the `Cached`-mode flip and the 3-C job.

### 🟠 Alert-noise fix — "tenant not resolvable" logs Critical on every retry (found 2026-07-22)

Symptom (observed): a fresh purchase whose RAU tenant isn't provisioned yet emits an `"Activated"` push
that 503s repeatedly with `tenant not resolvable`, and **each retry logs `LogSeverity.Critical`
(`SaasAcceleratorEventHandler.cs:329`)** — so a Critical-wired alert fires an email per attempt (~12 over
the outbox backoff). This is an **expected, self-clearing** state during onboarding (the tenant provisions
only when the customer completes AppAddin2 Setup), not an operational emergency.

`:329` logs *every* apply-error at Critical, then returns 503:
```csharp
Loging.Log($"SaasAcceleratorEvent {idempotencyKey}: {applyError}", Logger, LogSeverity.Critical);
return JsonResult(HttpStatusCode.ServiceUnavailable, "error", applyError);
```
Don't blanket-lower it — a genuine config error (`"no connection string for MasterDb…"`) should stay loud.
Make it **age-based**: unresolvable is normal for a while after purchase; still-unresolvable long after the
event means genuinely stuck.
```csharp
// Warning during the expected onboarding window; Critical only if the event is old (stuck tenant).
var stuck = (DateTime.UtcNow - modifiedUtc.Value) > TimeSpan.FromHours(12); // reuse a config knob if present
Loging.Log($"SaasAcceleratorEvent {idempotencyKey}: {applyError}",
    Logger, stuck ? LogSeverity.Critical : LogSeverity.Warning);
```
Zero alerts during normal onboarding, one near dead-letter if truly stuck. Also drop the **duplicate**
Critical inside the "no provisioned tenant DB" path (~`:589`) to Warning — it double-logs with `:329`.

### ✅ A dead-lettered `"Activated"` is HARMLESS for a first-time / just-wiped tenant

Because the onboarding timeline is unbounded (customer may take days across Customer-portal + AppAddin2),
the `Activated` push can dead-letter (~52h) before the tenant provisions. **That loses nothing here:**

1. Under `"Live"` the gate reads `SubscribedActive` from the **live Fulfillment pull**, not the cache — the
   `Activated` push only writes the display-only cached status.
2. When Setup completes, **`InitialiseSaasTenant` inserts the `Subscription` row with
   `MarketplaceSubscriptionStatus` from a fresh live pull** (`= Subscribed`) and `StateAsOfUtc = now` — the
   cache ends up correct **independent of the push**.
3. Even a late-delivered `Activated` (modifiedUtc = activation time) is **F2-guard-skipped as stale** once
   provisioning stamps a newer `StateAsOfUtc`. Deliver-late or dead-letter → identical outcome.

The **only** case a lost `Activated` matters: `"Cached"` mode **and** a resubscribe onto a *pre-existing*
tenant DB (provisioning doesn't re-run, so the push is the only thing flipping `Unsubscribed → Subscribed`)
— and even there the **daily reconcile** is the backstop. So: do not shorten the retry window or otherwise
engineer around the onboarding timeline; just fix the log severity above.

---

## 0. Why this exists

RAU runs `MarketplaceStatusSource = "Live"` today, so the AppAddin2 gate derives `SubscribedActive`
from a live Fulfillment pull and the tenant DB's cached `MarketplaceSubscriptionStatus` only feeds
display fields. In **`"Cached"`** mode the cache *becomes the gate* — so every status transition must
reach the cache via a push, or the gate desyncs. An audit of the accelerator found two transitions
that emitted no push; both are now fixed **on the sender side (accelerator repo, already done)**:

| Transition | Accelerator site | Signal now emitted |
|---|---|---|
| Activation → `Subscribed` (first subscribe **and** resubscribe) | `PendingActivationStatusHandler` | **`"Activated"`** (new event type) |
| Portal-initiated unsubscribe → `Unsubscribed` (Fulfillment DELETE path) | `UnsubscribeStatusHandler` | `"Unsubscribed"` (existing type) |

Both use `operationId = Guid.Empty` (no Marketplace op id on these paths), so the outbox idempotency
key is `{eventType}|{sub:N}|00000000…`. Payload shape is the standard signal: `subscriptionStatus`
is **already normalized to Marketplace canonical** by the sender (`SubscriptionStatusNormalizer`), so
`"Activated"` carries `subscriptionStatus = "Subscribed"`.

**This spec is the RAU-side work + the flip checklist. Until it lands, `"Activated"` is an unknown
event type that the receiver 202-drops (harmless under `"Live"`); do NOT flip to `"Cached"` before it.**

## 1. Receiver change — add the `"Activated"` dispatch case

`SaasAcceleratorEventHandler.cs`. Dispatch coverage today is `PlanChanged`, `Reinstated`, `Suspended`,
`Unsubscribed`, plus `TenantRegionFanOut` (Phase 3-B "Verified correct"). Add one case:

- **`"Activated"` → apply `subscriptionStatus` ("Subscribed") through the exact same
  `ApplyPushedSubscriptionStatusAsync` path `Reinstated` uses.** Functionally `Activated` and
  `Reinstated` are identical at the receiver (both write `Subscribed` under the F2 ordering guard and
  the F5 null-status rejection); the only reason for a distinct event name is honest logs
  (activation vs un-suspension). Alias it to the same handler rather than duplicating logic.
- **`"Unsubscribed"` from the portal path needs NOTHING new** — it's the same event type and payload
  shape the webhook path already sends; the existing case handles it.

Confirm the **Phase 3-A normalizer** treats `Activated`'s `subscriptionStatus = "Subscribed"` as
canonical `Subscribed` (it already is — this is a no-op check, not a change).

After this, verify `"Activated"` no longer reaches the `default`/unhandled branch (Phase 3-B F6).

## 2. Resolution & ordering (matters more once the cache gates access)

- **Tenant resolution on resubscribe.** The signal carries `assignedTenantId =
  subscription.PurchaserTenantId ?? Guid.Empty`. When present, the receiver resolves by tenant id
  directly — correct even though the tenant DB still holds the *old* subscription id. **Verify
  `PurchaserTenantId` is reliably populated at activation time**; if it can be empty, the receiver
  falls to `ResolveTenantBySubscriptionId`, which needs the resubscribe's `TenantRegionFanOut` (new
  sub id) to have landed first. Log a warning if an `"Activated"` arrives with `Guid.Empty` tenant id.
  - ⚠ **This fixes only the `Subscription` table, NOT `TenantRegions.SubscriptionId`.** The apply is
    tenant-keyed (`FarmId`), so the cache flips correctly; but `TenantRegions.SubscriptionId` keeps the
    **old** sub id (`ApplyPushedSubscriptionStatusAsync` anchors that column only when it is null, never
    when it differs — it is re-anchored to the new id only by a `TenantRegionFanOut`, which fires only if
    the customer re-selects region in Setup). That deliberately-stale column is exactly why the Phase 3-C
    purge must lean on **Stage A**, not the Stage B sub-id live-check — see the "Downstream dependency"
    section above and the 3-C trace. Both statements are individually correct; do not read this bullet in
    isolation from that constraint.
- **F2 guard interaction.** `"Activated"` carries `modifiedUtc = now`. Against a stale
  `Unsubscribed` row (older `MarketplaceStateAsOfUtc`), the guard applies it → cache flips to
  `Subscribed`. A genuinely newer push (e.g. an immediate suspend) still wins. This is exactly the
  resubscribe reactivation the whole change exists to deliver.
- **Brief staleness window.** Between the resubscribe purchase and the `"Activated"` push landing,
  the cache is still `Unsubscribed`; in `"Cached"` mode the customer is briefly gated out. Mitigate
  by keeping the outbox drain prompt; the reconcile is the ultimate backstop.

## 3. The flip checklist (do in order; each step is reversible)

1. **Deploy the accelerator** carrying the `"Activated"` / portal-`"Unsubscribed"` pushes (this repo).
   Inert under `"Live"`, so it can ship anytime ahead of the flip.
2. **Deploy this RAU receiver change** to **every region**. Still inert — `"Live"` ignores the cache.
3. **True-up the cache before flipping.** Run a full reconcile pass so every *active* tenant has
   `MarketplaceSubscriptionStatus = Subscribed` cached. A `null` cache is safe (`GetSetupStateInternal`
   falls back to live), but a **stale `Unsubscribed`** (left by a Live-mode unsubscribe→resubscribe)
   would block a real customer the instant you flip. Reconcile clears those.
4. **Flip `MarketplaceStatusSource` to `"Cached"`** — per region, ideally one region first.
   - ⚠ **Edit the `web.config` FILE, not an App Service Application Setting.** Both hosts read the
     physical file via `OpenMappedExeConfiguration`; a portal App Setting of the same name is injected as
     an env var and **silently ignored** (see the runbook amendment above).
   - ⚠ **Then restart the WebJob in that region and confirm it logged the new `MarketplaceStatusSource`
     at startup.** The Web app/AppAddin2 pick up the file edit on w3wp recycle immediately, but the
     WebJob (the tenant-processing gate) holds a config snapshot frozen at process start and only changes
     on restart.
   - **Rollback = set it back to `"Live"` (file edit) — no redeploy. NOT fully "instant": the Web app
     reverts on recycle, but you must restart the WebJob again for its gate to revert.** Do not assume a
     continuous WebJob auto-recycled on the config edit unless you have verified that behaviour in prod.
5. **Monitor:** gate-block rate, `"Activated"` apply counts, reconcile corrections, and any
   `"Activated"`-with-empty-tenant warnings. A spike in blocks ⇒ roll back to `"Live"` (per step 4's
   rollback note — WebJob restart included) and diagnose.

## 4. Tests (`Legeris.Office365.Tests`, NUnit — extend `UnitTest1-Phase3RauPush.cs`)

- `"Activated"` push → `ApplyPushedStatus` writes `Subscribed`; F2 guard: older `StateAsOfUtc`
  applies, newer skips, equal skips.
- Dispatch: `"Activated"` routes to the status-apply path, not the unhandled/default branch.
- **Cached-mode gate (the regression this enables):** cached `Subscribed` → `SubscribedActive` true;
  cached `Unsubscribed` → false; cached null → live fallback. Then the end-to-end shape: a resubscribe
  whose `"Activated"` push has landed flips the gate from blocked to active without a reconcile.
- Portal-`"Unsubscribed"` push → cache `Unsubscribed` (no new receiver code; guards regression).

## 5. Out of scope

- The accelerator sender changes — **already implemented** in this repo (this spec is the RAU half).
- Trial-expiry handling and the 7-day purge — see Phase 3-C.
- Any change to `InitialiseSaasTenant` — it is not on the resubscribe path; do not touch it here.
