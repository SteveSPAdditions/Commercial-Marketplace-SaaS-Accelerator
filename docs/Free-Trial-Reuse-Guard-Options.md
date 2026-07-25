# Free-Trial Reuse Guard — Options & Design Notes

**Status:** advisory / not yet implemented. Written 2026-07-25.

**Question:** can auto-activation detect that (a) the tenant is already known to RAU and (b) the newly
registered subscription is on the free-trial plan — and if so, withhold auto-activation and alert
SPAdditions by email, so a repeat free-trial purchase isn't silently provisioned?

---

## Short answer

Yes to both signals — and both are already available at the exact point where auto-activation fires,
`src/CustomerSite/Controllers/HomeController.cs:309`.

- **Is it the free-trial plan?** `newSubscription.PlanId` / `subscriptionData.PlanId` is in hand.
  `free-trial` is a real plan id in this offer — `src/Services/Services/AzureRegionService.cs:308`
  already maps `planId == "free-trial"` -> RAU `Status = "trial"`. Note `Plans`
  (`src/DataAccess/Entities/Plans.cs`) has **no** free-trial column, so this must be a config-driven
  plan-id list, not a schema lookup.
- **Is the tenant already known to RAU?** `IAzureRegionService.GetTenantRegionAsync(tenantId, ct)`
  returns `AzRegion != "?"` exactly when RAU has a `TenantRegions` row. That plumbing is proven — it
  is the retry pre-check at `src/Services/Services/AzureRegionService.cs:269-293`. Tenant id is
  available pre-activation as `subscriptionData.Purchaser.TenantId`, persisted at
  `src/Services/Services/SubscriptionService.cs:69`.

**The catch:** those two together give you *"known tenant re-purchasing a trial"*, which is **not the
same as "already consumed a trial."**

---

## Three oracles, in increasing durability

### A. Local accelerator DB — best signal, zero new dependencies

`Subscriptions` retains every historic row (including `Unsubscribed`) with `PurchaserTenantId` +
`AmpplanId` + dates. "Any prior row for this tenant on a trial plan, excluding the one just created"
answers the abuse question directly.

Blind spots: a DB wipe (see `tools/dev-reset-transactional.sql`), tenants known to RAU via
non-marketplace channels, and tenant-hopping.

### B. RAU region lookup — answers "known", not "trialed"

Function1 returns region + selectors only (`src/Services/Models/TenantRegionInfo.cs`). Blocking on
"known to RAU" alone would hit legitimate cases: an existing paying customer buying a second
subscription, a Legeris-direct customer moving to marketplace, or someone who deleted and
re-purchased during a failed setup. Use it as a **corroborating** signal, never the sole trigger.

### C. RAU trial history — the durable answer, needs a small RAU change

RAU's per-tenant `Subscriptions.Status` (`trial`/`live`) plus the `Marketplace*` cache columns are
the real record, and they survive an accelerator DB wipe.

**But** the Phase-3C purge deletes the tenant DB 7 days after unsubscribe (trials later at 30+7) —
that erases the evidence for precisely the abusive pattern being targeted: unsubscribe -> wait ->
re-purchase trial. So C only works if a **purge-surviving trial ledger** is added
(`tenantId, offerId, planId, firstTrialUtc, lastTrialSubscriptionId`), explicitly excluded from the
purge job. See `docs/Phase3C-Unsubscribe-Deprovisioning-Purge-Spec.md`.

---

## Recommended shape

**Decide locally, corroborate remotely, fail open, alert always.**

### Phase 1 — accelerator only, no RAU change

1. New `ApplicationConfiguration` rows (same pattern as `IsAutomaticProvisioningSupported`,
   `src/DataAccess/Migrations/Custom/BaselineV2_Seed.cs:380`):
   - `TrialPlanIds` (csv, default `free-trial`)
   - `TrialGuardMode` (`Off|AlertOnly|Block`, ship as `AlertOnly`)
   - `TrialAbuseAlertEmailTo`
   - `TrialGuardExemptTenantIds`
2. New `ITrialEligibilityService` in `Services` returning a verdict
   `{ IsTrialPlan, PriorTrialLocally, KnownToRau, RauRegion, Decision }`. Only calls RAU when
   `IsTrialPlan` — keeps the landing path fast for paid purchases — and treats any `IsFallback` /
   all-regions-failed result as *unknown -> allow*. Needs one repo method:
   `ISubscriptionsRepository.GetByPurchaserTenantId`.
3. Gate **both** activation entry points, or the block is trivially bypassed:
   - the auto-activate branch at `src/CustomerSite/Controllers/HomeController.cs:309`
   - the manual Activate branch at `src/CustomerSite/Controllers/HomeController.cs:737`
     (the customer can otherwise just click Activate on the subscriptions list)
4. Customer-facing landing: a new view in the style of the existing `View("SessionExpired")`
   precedent — "your subscription is under review, we'll be in touch" — rather than an error page.
5. Operator override already exists: AdminSite's Activate action
   (`src/AdminSite/Controllers/HomeController.cs:449`) completes the held subscription after review.
   No new UI needed.
6. Audit: `SubscriptionAuditLogs` row + `ApplicationLog`, so it is visible in AdminSite rather than
   only in App Insights.

### Phase 2 — RAU-side (optional, for durability)

Add a `tenant-eligibility` query on the existing Legeris signaling receiver (reuses
`LegerisSignalingEndpointUrl` + HMAC secret, already configured) returning
`{ knownTenant, currentStatus, priorTrialUtc, eligibleForTrial }`, backed by the purge-surviving
ledger. Accelerator prefers that answer, falls back to local.

---

## Who sends the alert email

**The accelerator.** It is where the decision is made, and it already has SMTP config,
`EmailHelper` / `IEmailService`, and AdminSite-editable `EmailTemplate` rows — so add a template row
keyed on a new status (tokens for tenant id, subscription id, purchaser email, prior trial date /
sub id) rather than hard-coding the body. Send best-effort inside a try/catch, matching the existing
non-fatal post-steps at `src/CustomerSite/Controllers/HomeController.cs:391-400`.

Belt-and-braces: **also** enqueue a `TrialActivationBlocked` outbox event via
`SubscriptionSignalService`. Email is fire-and-forget; the outbox retries for ~52h and gives ops a
durable record.

---

## Open decisions

1. **Block, or activate-and-flag?** Blocking has a real cost: an unactivated subscription is
   eventually auto-cancelled by Microsoft, so a false positive strands a legitimate customer
   mid-purchase and needs an operator SLA. Activate-and-flag (provision normally, alert, then
   unsubscribe manually if warranted) carries none of that risk and catches 100% of the same cases.
   **Recommendation: ship `AlertOnly` first**, watch the real hit rate for a few weeks, then flip to
   `Block` if the signal proves clean.
2. **Verify in Partner Center whether `free-trial` is a $0 *plan* or the free-trial *flag* on a paid
   plan.** If it is a free plan, Microsoft enforces nothing and re-purchase is unlimited — this guard
   is genuinely needed. If it is the trial flag, Microsoft's own one-trial-per-customer rule may
   already cover most of it, and the cheaper fix is commercial (make the plan private, or gate it)
   rather than code.
3. **Tenant-hopping is out of scope** for any of this — a determined abuser creates a new tenant.
   This raises the bar and gives visibility; it does not close the door. Worth agreeing that is the
   goal before building it.

---

## Related

- `docs/Phase3C-Unsubscribe-Deprovisioning-Purge-Spec.md` — the purge that would erase trial evidence
- `docs/Phase3D-Cached-Status-Mode-Enablement-Spec.md` — `"Activated"` receiver + Cached-mode flip
- `docs/Marketplace-SaaS-RAU-Test-Plan.md` — activation-path test cases (C1, C7)
