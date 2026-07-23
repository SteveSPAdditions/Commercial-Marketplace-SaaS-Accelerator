# E5 region-selector consolidation — make Function1 the single authority

> **Multi-codebase spec.** Four owners, each with a clearly-scoped task below:
> - **[A] RAU Web app** — `D:\VSTFSWork\Legeris for SharePoint\Legeris.Office365\Legeris.Office365Web` (VS-Claude; TFVC, .NET 4.7.2, MSBuild)
> - **[B] AzRSvc / Function1** — `D:\VSTFSWork\Legeris for SharePoint\ReadAndUnderstoodAzRSvc` (VS-Claude; .NET isolated Function)
> - **[C] shared-functions** — `d:\VSTFSWork\SPfx\shared-functions` (SPFx/TS)
> - **[D] the three SPFx packages** that consume shared-functions — rebuild/redeploy
> - **[E] SaaS Accelerator portal** — this repo (already scoped; see §E)
>
> Read each repo's own `CLAUDE.md` first. Builds on the existing Phase 1/4 "ferry Web.config → `/azuremdbs`
> → `ConnectionStringManager`" pattern (`AzureMDbs.cs` `SubscriptionProvider`/`EntraAppIdsByProvider`).

## 0. Problem & decision

The "E5 tenants also get **DEV** and **LH** as selectable regions" rule is currently **client-side only**
(shared-functions `getAzureRegionSelectors`). The SaaS Accelerator portal has no shared-functions, so an E5
tenant onboarding through the portal sees only `USA/UK/CA` and can't be placed in DEV/LH. Duplicating the
rule into the portal would be a **third** copy of the E5 list.

**Decision:** make **Function1 the single authority** for the *selector list*. It already receives
`tenantId` per request and is called by every consumer. Gate on an `E5TenantIds` set **ferried from
Web.config via `/azuremdbs`** (same pattern as the Phase 4 fields). Every consumer — SPFx and the portal —
then gets the correct, tenant-aware list from one place.

**⚠ Drift already happened (reconcile first).** The two lists are out of sync **today**:
- `Web.config:343` `E5TenantIds` = `71150c86…, 71aa0f4f…, 5e19255f-004b-4c0a-bcdf-9e04e67f0d69`
- shared-functions `E5TenantIds` = `71aa0f4f…, 71150c86…, 365161d7-724c-4608-b963-3d8d4bccf207`

The third GUID differs. **Before anything else, decide the correct E5 set and make `Web.config:343` the
canonical value.** (Note the Web.config list also serves an existing purpose — blocking real-tenantId
TenantRegion deletion — so confirm the reconciled set is valid for *both* uses.)

## 1. Invariants (all owners)

1. **E5-gated, always.** DEV/LH are emitted **only** when `tenantId ∈ E5TenantIds`. A customer tenant must
   never see DEV or LH. The gate is the entire safety mechanism.
2. **Selector vs URL are different concerns.** Function1 owns the *selector list* (which regions are
   pickable). The **LH → `https://localhost:44376` URL is machine-specific** and must **stay client-side**
   (shared-functions `azureAppUrlMap`) — a shared server cannot know a developer's local URL. So LH's
   *selector* moves to Function1, but LH's *URL mapping* does not.
3. **Copy, never mutate the cache.** `ConnectionStringManager.GetAzureRegionSelectors()` returns the live
   static list reference. Appending to it corrupts the cached list for every later request and grows it
   unboundedly. Build a fresh list per request.
4. **Selectors and master-DBs move together.** If Function1 emits a DEV/LH *selector*, `AzureRegionMasterDbs`
   must also contain DEV/LH keys, or routing re-hits `key 'DEV' not present` (the CS2880 failure). RAU
   Web.config already has `MasterDbLH`/`MasterDbDEV`.
5. **Defensive consumption + web-app-first.** Never index the ferried map blindly; a missing/empty
   `E5TenantIds` = "no augmentation," never a throw. Deploy the Web app (producer) before AzRSvc (consumer).

## A. RAU Web app — ferry `E5TenantIds` through `/azuremdbs`  (VS-Claude)

1. **Canonicalise `Web.config:343` `E5TenantIds`** to the reconciled set (see §0 drift note).
2. **Add `E5TenantIds` to `AzureMDbsResponse`** (`Legeris.Office365.ServiceModel\AzureMDbs.cs`) — a
   `List<string>` (or CSV `string`), alongside `SubscriptionProvider`/`EntraAppIdsByProvider`.
3. **Populate it** in the `/azuremdbs` handler from the `E5TenantIds` AppSetting (parse the CSV once).
4. **Ensure `AzureRegionMasterDbs` carries `DEV` and `LH`** (from `MasterDbDEV`/`MasterDbLH`) so consumers
   can route those regions (invariant #4). These are always present server-side; they're only *offered* to
   E5 tenants by Function1.
5. **Deploy FIRST** and verify `GET /api/azuremdbs?format=json` returns the new `E5TenantIds` field before
   AzRSvc is shipped.

## B. AzRSvc / Function1 — emit DEV+LH selectors for E5 tenants  (VS-Claude)

1. **Load `E5TenantIds` at startup** in `ConnectionStringManager` from the azuremdbs payload
   (`ConnectionStringManager.cs:~233`), next to the existing Phase 1/4 fields. Store as a
   `HashSet<string>` (case-insensitive). Missing/empty ⇒ empty set (invariant #5 — never throw).
2. **In Function1, build a COPY** of `GetAzureRegionSelectors()` per request; **iff** the request's
   `tenantId ∈ E5TenantIds`, append `{Key:"DEV",Text:"DEV"}` and `{Key:"LH",Text:"Local Host"}` to the copy.
   Return the copy. Do **not** mutate the cached list (invariant #3).
3. **Do not emit any LH *URL*** — Function1 emits the LH *selector* only. The URL stays client-side (§C).
4. **Deploy SECOND**, after §A is live and verified.

## C. shared-functions — retire the selector push, KEEP the URL map  (SPFx/TS)

`d:\VSTFSWork\SPfx\shared-functions\src\GetAppEndPointByAzrSvc.ts`:

1. **Remove the DEV/LH push from `getAzureRegionSelectors`** (lines 49-52) — Function1 now supplies them.
   As an interim safety before Function1 ships everywhere, make it **idempotent** (only push if not already
   present) rather than deleting outright, then delete once §B is live in all regions.
2. **KEEP `azureAppUrlMap`'s E5 override** (lines 30-33) — `LH → https://localhost:44376` is machine-specific
   and cannot come from the server. `E5TenantIds` therefore **stays in shared-functions for this URL map**;
   it is *not* fully retired.
3. **Fix the latent mutation bug** while here: both `getAzureRegionSelectors` (push onto the singleton list)
   and `azureAppUrlMap` (mutate `AzureMap`) write to the shared `TenantRegion.instance`. Guard against
   duplicate appends (dedupe by key) — masked today only because the instance is replaced per fetch.
4. If shared-functions keeps its own `E5TenantIds` for the URL map, **point it at the same reconciled set**
   as `Web.config:343` and add a comment that Web.config is canonical.

## D. The three SPFx packages — rebuild on updated shared-functions

The three packages that consume shared-functions must be **rebuilt and redeployed** on the §C version.
Until a package is rebuilt, an E5 tenant loaded through it would get **duplicate DEV/LH** (Function1's +
the old push) — which the §C.1 idempotent guard prevents in the interim. Enumerate the three packages and
track their redeploy; DEV/LH E5 tenants must be exercised only on rebuilt packages.

## E. SaaS Accelerator portal — consume live selectors  (this repo)

`AzureRegionService.GetTenantRegionAsync` currently **discards** `AzureRegionSelectors` on an
`AzRegion="?"` response (`AzureRegionService.cs:87-91`), so a new/unassigned tenant never sees Function1's
list. Change resolution priority to:
1. a response that **identifies** the tenant (`AzRegion != "?"`) → return it + its selectors (unchanged);
2. else the **first 200 `AzRegion="?"` response with a non-empty selector list** → return
   `{AzRegion:"?", AzureRegionSelectors:<live>, IsFallback:false}`;
3. else the static `AzureRegionSelectorsFallback` (genuine last resort).

Result: the portal renders Function1's list verbatim — E5 tenants get `USA/UK/CA/DEV/LH` with **zero E5
logic in the portal**. (The portal never needs the LH *URL* — that's the SPFx launcher's job; the portal
only writes `TenantRegions.AzureRegion` and lets RAU route `MasterDb{region}`.)

## Deploy sequence (ordered — the CS2880 lesson)

1. **[A]** Web app: canonical `E5TenantIds` + `AzureMDbsResponse.E5TenantIds` + DEV/LH in
   `AzureRegionMasterDbs`. Verify `/api/azuremdbs`.
2. **[B]** AzRSvc/Function1: consume defensively, emit DEV+LH selectors (copy). Deploy to **every** region.
3. **[C]** shared-functions: idempotent-guard (interim) → remove push; keep URL map.
4. **[D]** rebuild the three SPFx packages.
5. **[E]** accelerator: consume-live-selectors change (can ship any time; inert until Function1 emits).
6. Only after all E5-serving AzRSvc regions are on **[B]**, delete the shared-functions selector push (§C.1).

## Validation

- **[A]** `GET /api/azuremdbs?format=json` contains `E5TenantIds` and DEV/LH master-DB keys.
- **[B]** Function1 for an E5 tenantId returns `…,DEV,LH`; for a non-E5 tenantId returns only `USA/UK/CA`;
  the cached list is unchanged across repeated E5 calls (copy proof). AzRSvc-first (azuremdbs missing the
  field) does not throw.
- **[C]** `getAzureRegionSelectors` no longer double-appends; `azureAppUrlMap` still returns
  `localhost:44376` for LH on an E5 tenant.
- **[E]** portal onboarding of an E5 tenant with no `TenantRegions` row shows DEV/LH and can Save into DEV.
- **Customer regression:** a non-E5 tenant sees only `USA/UK/CA` everywhere.
