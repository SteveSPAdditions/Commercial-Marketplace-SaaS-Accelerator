# Phase 3-D — Enabling `MarketplaceStatusSource = "Cached"` (spec for Claude-in-VS)

> **Audience:** Claude running in Visual Studio with the **Legeris ("Read and Understood")** solution
> open (`D:\VSTFSWork\Legeris for SharePoint`). **TFVC**, **.NET Framework 4.7.2**,
> **ServiceStack + OrmLite + Newtonsoft**, MSBuild/VS. **Read the repo's root `CLAUDE.md` first.**
> Companion to [Phase3B-RAU-Receiver-Hardening-Spec.md](Phase3B-RAU-Receiver-Hardening-Spec.md)
> and [Phase3C-Unsubscribe-Deprovisioning-Purge-Spec.md](Phase3C-Unsubscribe-Deprovisioning-Purge-Spec.md).

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
