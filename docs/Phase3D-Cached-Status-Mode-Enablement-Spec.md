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

### 🔴 TWO BLOCKERS before flipping to `"Cached"` — both verified in the accelerator repo

**Blocker 1 — `"Activated"` fires at most ONCE per subscription id (sender-side).** The enqueue dedup
`SubscriptionSignalService.cs:54-58` uses key `{eventType}|{sub:N}|{operationId:N}` with
`operationId = Guid.Empty` on both new paths, so the key is a **constant per (eventType, subscriptionId)**.
`GetByIdempotencyKey` (`NotificationOutboxRepository.cs:100-104`) does **not** filter on `DeliveredUtc`,
and `MarkDelivered` (`:65-76`) **retains** the delivered row. So the first activation's row lives forever
and suppresses every later `"Activated"` for that subscription — i.e. the **resubscribe case §0 says this
exists to cover**. It would pass a single-cycle test and fail silently on the second cycle. Same
single-shot property affects the portal-`"Unsubscribed"` path. **Fix is sender-side** (out of this spec's
scope but gates the flip): make the key unique per occurrence (include `modifiedUtc` or a fresh GUID
rather than `Guid.Empty`), or have `GetByIdempotencyKey` match only undelivered rows (the former is
safer — the latter changes retry semantics). This depends on whether a resubscribe reuses the AMP
subscription id (see below).

**Blocker 2 — §3's "rollback is instant, no redeploy" is FALSE for the WebJob.** The WebJob (which runs
the tenant-processing gate — the consumer that actually blocks customers) snapshots config once at
startup via `OpenMappedExeConfiguration` (`Jobs.Events\Program.cs:79-80`); it does not re-read on access.
Editing `Web.config` recycles the app pool (Web app + AppAddin2 see it immediately) but leaves the
**running WebJob on the old value**. §3 steps 4 AND 5 must add **"restart the WebJob in the target
region"** — to both the flip and the rollback — or the rollback claim is wrong exactly when it's needed.

### 🟠 Open question that sets both blockers' severity + a 3-C interaction

**Does a resubscribe reuse the AMP subscription id?**
- **New id** → Blocker 1 doesn't bite `Activated` (keys differ), but `TenantRegions.SubscriptionId` holds
  the OLD id until a `TenantRegionFanOut` lands; `ApplyPushedSubscriptionStatusAsync` only anchors that
  column when it is **null**, never when it differs. ⚠ **This breaks Phase 3-C Stage B**, which
  live-checks using `TenantRegions.SubscriptionId`: against the stale dead id it returns `Unsubscribed`
  and would **confirm a purge for a customer who resubscribed under a new id**. Fold into 3-C.
- **Same id reused** → Blocker 1 bites hard; `"Activated"` never fires on resubscribe.

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
4. **Flip `MarketplaceStatusSource` to `"Cached"`** in `Web.config` — per region, ideally one region
   first. **Rollback = set it back to `"Live"`; instant, no redeploy.**
5. **Monitor:** gate-block rate, `"Activated"` apply counts, reconcile corrections, and any
   `"Activated"`-with-empty-tenant warnings. A spike in blocks ⇒ roll back to `"Live"` and diagnose.

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
