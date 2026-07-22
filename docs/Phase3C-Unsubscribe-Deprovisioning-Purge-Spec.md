# Phase 3-C — Unsubscribe deprovisioning & 7-day purge (spec for Claude-in-VS)

> **Audience:** Claude running in Visual Studio with the **Legeris ("Read and Understood")** solution
> open (`D:\VSTFSWork\Legeris for SharePoint`). **TFVC**, **.NET Framework 4.7.2**,
> **ServiceStack + OrmLite + Newtonsoft**, built with **MSBuild/VS**. **Read the repo's root
> `CLAUDE.md` first.** Companion to the Phase 3 push spec and
> [Phase3B-RAU-Receiver-Hardening-Spec.md](Phase3B-RAU-Receiver-Hardening-Spec.md).
>
> **Scope of this pass: UNSUBSCRIBE only.** Trial-expiry purge is explicitly out of scope and must
> not be implemented here, even though the same job will later grow a trial arm.

---

## ⚠ STATUS — 2026-07-21: REVIEWED AND REDESIGNED, JOB NOT BUILT

**Read this section before implementing anything below — §3 and §4 have materially changed, and two
statements in §1/§4 are factually wrong.**

### The job is now NON-DESTRUCTIVE (supersedes §3)

The job **does not drop databases**. It detects candidates and emails **team@spadditions.com** a link to
an operator-initiated endpoint that performs the deprovisioning. This removes the catastrophic failure
modes rather than mitigating them.

The link deliberately **omits a `code=`** the operator must look up — an anti-fat-finger interlock plus
proof they identified the right tenant.

- **Interlock value = `TenantRegions.SubscriptionId`.** Per-tenant, GUID entropy, **master-resident** so
  it survives the tenant-DB drop and needs no Graph call or live consent.
- *Rejected:* the per-tenant `EnterpriseApplicationObjectId` — it only exists at token-mint time (it is a
  response field on `LfsoValidate`/`LfsoLibraryEnabledStatus`, never persisted) and is **unobtainable
  once consent is withdrawn**, which is the common post-unsubscribe state.
- *Rejected:* a publisher-side constant (the SP Additions app's own object id) — identical for every
  tenant, so it proves nothing about *which* tenant and stops being a speed bump after the first use.
- **The code must NOT appear in the notification email**, or the interlock is theatre.
- **`code=` is a second factor, not authorisation.** The endpoint still needs real auth: the code proves
  *which*, auth proves *who*.
- The endpoint must **re-run the Stage B live check at click time** — the email may be days old. This is
  where §1's rule now lives.
- The customer email (§4) is sent **by the endpoint after a successful purge**, not with the ops
  notification — do not tell a customer their data is gone before it is.
- §5's config keys now govern *notification*, not deletion, and should be renamed accordingly.

### Factual corrections to this spec

- **§1 is correct on the mode.** `Web.config` reads `MarketplaceStatusSource = "Live"` (verified on disk
  2026-07-22). *(An earlier revision of this note claimed it read `Cached` — that was wrong.)* The
  must-live-verify-before-deleting conclusion holds regardless of mode, and would hold even more strongly
  if it were ever flipped to `Cached`.
- **§4's `AzureRegionSelectedByUpn` does not exist** anywhere in the solution. The only address captured
  at setup is **`TenantSiteCollections.BackupEmailAddress`** (with `BackupEmailAddressValidated`).
  Two consequences: it is **tenant-DB-resident**, so it must be captured **before** the drop; and it is
  **per site collection**, so a tenant may have several — prefer rows where
  `BackupEmailAddressValidated != null`.

### Prerequisites — both DONE

| | |
|---|---|
| **Stage A could never select anyone.** The reconcile stamped `MarketplaceStateAsOfUtc = now` every pass, so it never aged past a day and `StateAsOfUtc <= UtcNow - 7 days` was never true. The job would have been silently inert forever. | ✅ Fixed. `SaaSInitialiseSubscriptionsHandler.NextStateAsOfUtc` advances the clock **only when the live status differs from the cached status**, stamps a baseline when null, otherwise leaves it. Note this also means §2's "the unsubscribe time the push carried" is only true for push-populated rows; reconcile-populated rows carry the first-observation time (conservative — it can only delay, never trigger early). |
| **§2's "404 → purge" was unimplementable and failed dangerously.** `GetSubscriptionInfo` returned `null` for 404, 403, 5xx, timeouts *and* missing config alike — so an expired `MarketplaceFulfillmentClientSecret` would have made **every** subscription look purged. | ✅ Fixed. New `MarketplaceFulfillmentClient.GetSubscription(...)` returns `MarketplaceSubscriptionLookup { Outcome = Found \| NotFound \| Indeterminate, Info, Detail }`. **Only an explicit HTTP 404 yields `NotFound`.** The purge path may act on `NotFound` and must **never** act on `Indeterminate`. `GetSubscriptionInfo` is retained as a wrapper so existing fail-closed gate callers are unchanged. |

### Still open before building the job

1. **§3's revoke-fails-blocks-drop deadlocks on the most likely state.** After unsubscribe customers
   commonly remove the enterprise app / withdraw consent, so the Graph mint fails (`AADSTS700016`),
   revoke "fails", and the tenant is dead-lettered and **never purged** — the opposite of the GDPR
   intent. Needs two branches: *consent/app gone* → nothing to revoke and nothing we can do → **success,
   proceed**; *transient Graph failure* → abort and retry.
2. **§3 step 5 deletes only the home-region `TenantRegion`, but those rows are replicated** to every
   master. Note `Migrate.cs:104` has exactly that delete **deliberately commented out** — understand
   that precedent before re-introducing it.
3. **Reuse the existing purge path.** `Migrate.cs` already deletes `MdbTenant` (cascading
   `SiteCollections` via FK), deletes `ZoHoSubscriptions` rows, and has a `DeleteMigratedDatabase` arm
   that drops a tenant DB.
4. **Confirm Azure SQL PITR retention** per region and write it into the spec — it is the real recovery
   path if a purge turns out to be wrong, and the audit record alone is not one.
5. **Dedup the ops notification** (marker in master, not the tenant DB) so team@ is not emailed daily
   per tenant.
6. §5's "run after reconcile" is not guaranteed — reconcile has its own daily gate. Harmless, since
   Stage B is the real guard, but do not depend on the ordering.

### 🔑 Stage A is the real guard, not Stage B — and a hard ordering prerequisite (accelerator-side trace, 2026-07-22)

Traced end-to-end from the accelerator. **Correction to item 6 above: "Stage B is the real guard" is
wrong.** The real guard is **Stage A + the accelerator's `"Activated"` push**, because that push updates
the cache **by tenant, not subscription id**:

- Resubscribe = a **new** AMP subscription id (`SubscriptionsRepository.Save` keys on `AmpsubscriptionId`;
  Marketplace issues a new GUID per purchase).
- The `"Activated"` push fires on activation (before Setup) carrying `assignedTenantId = PurchaserTenantId`.
- The receiver resolves by **tenant id** and updates `Select<Subscription>(s => s.FarmId ==
  dbSideTenantGuid)` (`SaasAcceleratorEventHandler.cs:599`) — tenant-keyed, **independent of the stale
  `TenantRegions.SubscriptionId`**. So an active resubscriber's cache flips to `Subscribed`, and **Stage A
  never nominates them → Stage B never runs for them.**

Stage B is only reached for still-`Unsubscribed`-cached tenants, and its `TenantRegions.SubscriptionId`
live-check **can legitimately be stale**: that column is only re-anchored to the new id by a
`TenantRegionFanOut` (`RegisterInRegionAsync:468`), which fires only if the customer re-selects region in
Setup — but the `TenantRegion` row is keyed by **tenant** and persists, so the app can be used on
resubscribe without ever re-firing the fan-out. So Stage B's recorded id can point at the old, deleted
subscription and 404 → falsely confirm a purge.

**The dangerous window needs BOTH:** the Phase 3-D `"Activated"` receiver not yet deployed (push
202-dropped, cache stays `Unsubscribed`) **and** the fan-out not re-landed (stale sub id).

**Therefore — hard prerequisite: do NOT enable this purge job until the Phase 3-D `"Activated"` receiver
is deployed to every region** (see [Phase3D-Cached-Status-Mode-Enablement-Spec.md](Phase3D-Cached-Status-Mode-Enablement-Spec.md)).
Once it is, Stage A's per-tenant cache is authoritative and the stale-sub-id problem never reaches a real
customer. Belt-and-suspenders (optional): the operator endpoint should treat a live `NotFound`/
`Unsubscribed` as **inconclusive** (abort, do not purge) when the tenant DB cache reads `Subscribed`.

### Endorsed as-is

Two-stage nominate-then-live-verify; re-verifying inside the action boundary; revoke-before-drop
ordering; `Enabled=false` + `DryRun=true` + batch cap defaults (add: require **both** `Enabled=true`
**and** `DryRun=false` to act, and log the mode at startup); keeping it out of the reconcile's failure
domain; trial-expiry excluded.

---

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
