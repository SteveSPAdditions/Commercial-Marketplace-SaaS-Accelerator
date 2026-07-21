# Phase 3-C — Unsubscribe deprovisioning & 7-day purge (spec for Claude-in-VS)

> **Audience:** Claude running in Visual Studio with the **Legeris ("Read and Understood")** solution
> open (`D:\VSTFSWork\Legeris for SharePoint`). **TFVC**, **.NET Framework 4.7.2**,
> **ServiceStack + OrmLite + Newtonsoft**, built with **MSBuild/VS**. **Read the repo's root
> `CLAUDE.md` first.** Companion to the Phase 3 push spec and
> [Phase3B-RAU-Receiver-Hardening-Spec.md](Phase3B-RAU-Receiver-Hardening-Spec.md).
>
> **Scope of this pass: UNSUBSCRIBE only.** Trial-expiry purge is explicitly out of scope and must
> not be implemented here, even though the same job will later grow a trial arm.

## 0. Policy (locked)

- On unsubscribe, RAU keeps everything for a **7-day grace window**, then purges.
- **Data continuity on resubscribe within the window is a feature** — a customer who returns inside
  7 days gets their tenant DB, `Sites.Selected` grants, and consents back with zero re-provisioning.
- After 7 days unsubscribed with no resubscribe: **purge the tenant DB, revoke the access RAU
  controls, and email the customer admin** what was removed and what only they can remove.
- Trial plans (30-day) will later get their own 7-day-after-expiry purge. **Not now.**

## 1. Why the job cannot trust the cached status (the load-bearing constraint)

RAU runs `MarketplaceStatusSource = "Live"` (`Web.config`). Verified consequence: after a customer
**resubscribes inside the grace window**, the accelerator emits **no activation status push**, and
`InitialiseSaasTenant` is **not** called (the AppAddin2 shell gates on the internal `Status`, which
survives unsubscribe). So the tenant DB's cached `MarketplaceSubscriptionStatus` **stays
`Unsubscribed`** until the next daily reconcile — even though the customer is live again.

**Therefore the purge job MUST re-verify the LIVE Fulfillment status immediately before deleting
anything.** A job that selected purge candidates by cached `MarketplaceSubscriptionStatus =
Unsubscribed` alone would **delete a customer who just resubscribed**. This is the single most
important rule in this spec.

## 2. Selection — who is a purge candidate

Two-stage: a cheap DB filter to nominate, then an authoritative live re-check to confirm.

**Stage A — nominate (master + tenant DB, cheap).** A tenant is a *candidate* when:
- Its home-region tenant DB `Subscription` row has `MarketplaceSubscriptionStatus = 'Unsubscribed'`
  (canonical casing per Phase 3-A normalization), **and**
- `MarketplaceStateAsOfUtc <= UtcNow - 7 days` — `StateAsOfUtc` is when the state became true at the
  source (the unsubscribe time the push carried), which is the correct clock for the grace window.
  A null `StateAsOfUtc` is **not** a candidate (unknown unsubscribe time — leave for reconcile to set).

**Stage B — confirm (live Fulfillment, authoritative).** For each candidate, call the Fulfillment API
`GetSubscription(TenantRegions.SubscriptionId)`:
- **Still `Unsubscribed`** → proceed to purge.
- **Anything else** (`Subscribed`, `Suspended`, …) → the customer resubscribed or was reinstated
  inside the window; **abort purge for this tenant**, and refresh the cached snapshot from the live
  pull (same write the reconcile does) so the stale row self-heals. Log it as a save, not an error.
- **Not found / 404** (Microsoft purged it) → treat as confirmed-gone; proceed to purge.

Respect the LH/DEV isolation rule (Phase 3-B F3) when resolving regions — a dev run must never touch
production tenants and vice-versa. Reuse the same `IsRegionInThisIsolationScope` helper.

## 3. Deprovisioning — order matters (revoke before you lose the keys)

Per confirmed tenant, in this order. Each step idempotent; a step that finds its target already gone
is a success, not a failure.

1. **Re-verify (Stage B) one more time inside the per-tenant transaction boundary** — the gap between
   selection and action can be minutes; a resubscribe in that gap must still abort.
2. **Revoke `Sites.Selected` grants.** While the runtime app's consent still exists, enumerate the
   sites RAU was granted and `DELETE /sites/{siteId}/permissions/{permId}` for each. Do this FIRST —
   once the app registration/secret is gone you can no longer call the customer tenant to clean up.
3. **Disable/rotate any per-tenant RAU credentials** RAU owns (not the customer's enterprise-app
   consent — that's theirs; see step 6).
4. **Purge the tenant database.** `DROP DATABASE [<per-tenant db>]` from master (Azure SQL: clear
   pools first, no `SET SINGLE_USER`; reuse the orphan-drop retry loop already in
   `InitialiseSaasTenant.cs:171-201`).
5. **Delete master rows:** `TenantRegion` (home region), `MdbTenant`, and any stray SaaS
   `ZoHoSubscriptions` pending rows (should be none post-Phase-3-B, but delete defensively).
6. **Email the customer admin** (see §4).
7. **Audit** — write a purge record (tenant id, subscription id, region, sizes/rowcounts removed,
   timestamps, operator=job) to a durable log that survives the tenant DB it describes. GDPR: this is
   the minimal retained proof of deletion.

Failure handling: if step 2 (revoke) fails, **do not proceed to step 4 (DROP)** — a half-deprovisioned
tenant with a dropped DB but live grants is the worst state. Dead-letter the tenant, alert, retry next
run. Steps 4–5 failing after a successful revoke are safe to retry.

## 4. The customer email

- **Recipients:** the tenant's admin UPN(s) captured during setup/consent (the region-select actor
  `AzureRegionSelectedByUpn` and/or admin-consent grantor). If none resolvable, log `Critical` and
  still purge — but flag for manual follow-up.
- **Content:** (a) confirm the RaU data for their tenant was deleted after the 7-day grace; (b) list
  the **residual artifact only they can remove** — the RaU enterprise application / service principal
  in *their* Entra tenant (Enterprise applications → find RaU app → Delete), because RAU cannot remove
  its own SP from a customer tenant; (c) how to return (re-purchase the offer; a fresh tenant DB is
  provisioned on next setup).
- Send via the existing RAU mail path; plain, no marketing. Idempotent: record "email sent" so a
  retried purge doesn't re-send.

## 5. Hosting, scheduling, config

- Lives in **`Legeris.Office365.Jobs.Events`** (the WebJob that already runs reconcile), as a separate
  scheduled entry point — do **not** fold it into the reconcile pass (different failure domain).
- **Daily** cadence is sufficient (grace is measured in days). Run it AFTER reconcile so any
  same-day resubscribe has already refreshed the cache — though Stage B's live re-check is the real
  guard, this just reduces needless live pulls.
- **Config keys** (App.config / Web.config, ASCII-only):
  - `UnsubscribeGraceDays` — default `7`. Never hard-code the window.
  - `UnsubscribePurgeEnabled` — default `false`. Ship inert; flip on per environment after a dry run.
  - `UnsubscribePurgeDryRun` — default `true`. Logs every action it *would* take, deletes nothing.
  - `UnsubscribePurgeMaxPerRun` — batch cap (e.g. `25`) so a bad run can't mass-delete.

## 6. Tests (`Legeris.Office365.Tests`, NUnit — extend, don't start a new fixture)

- Candidate `Unsubscribed` + `StateAsOfUtc` exactly 7 days old → selected; 6 days → not selected.
- Null `StateAsOfUtc` → never selected.
- **Resubscribe-in-window guard:** candidate whose cached status is `Unsubscribed` but whose live
  Fulfillment pull returns `Subscribed` → **not purged**, cache refreshed. (This is the regression
  this spec exists to prevent — mirror Phase 3-B F2's intent.)
- Revoke-fails → DB not dropped, tenant dead-lettered.
- Dry-run → zero deletes, full action log.
- Idempotency → second run over an already-purged tenant is a clean no-op, no duplicate email.

## 7. Explicitly out of scope (log separately, do not build here)

- **Trial-expiry purge** (30-day trial + 7-day grace) — a later arm of the same job.
- **Option A** (accelerator emitting an activation status push) — only needed if RAU moves to
  `MarketplaceStatusSource = "Cached"`; unrelated to purge. See the Phase 3-C companion notes.
- Any change to `InitialiseSaasTenant` — it is not on the resubscribe path, so it is the wrong place
  for anything in this area.
