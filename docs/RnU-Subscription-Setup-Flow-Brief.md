# Read & Understood — Subscription & Setup Flow

**Purpose:** Brief describing the end-to-end journey for a customer subscribing to **Read & Understood** as a transactable SaaS offer from the Azure Portal, including the five custom setup steps we have added on top of the standard Commercial Marketplace SaaS Accelerator flow.

**Audience / use:** Input for changes to the Read & Understood **setup app (`appaddin2`)**. `appaddin2` does **not** drive the five steps — the customer portal setup wizard does. `appaddin2`'s job is to **verify that the steps are complete** before it provisions or grants access: specifically, before **creating the tenant's database on first use**, and before **allowing app access** thereafter (the exact verification point depends on timing — see Section 4). This document is the high-level contract `appaddin2` verifies against — the steps, their order, gating, and the permissions/state each one establishes.

---

## 1. Overview

Read & Understood is sold as a transactable SaaS offer (listing `WA200007564`) on the Azure Commercial Marketplace. A customer purchases it from their own **Azure Portal**, lands on our **customer portal** to activate, and then completes a **five-step setup wizard** before the application is operational against their SharePoint Online tenant.

The setup wizard is the Read & Understood-specific extension, **driven by the customer portal**. The standard accelerator only handles purchase, fulfilment, and activation; everything in **Section 3** below is custom. `appaddin2` is downstream of all of it: it **reads the resulting setup state and verifies the steps are complete** before creating the database on first use or allowing app access (Section 4).

```
Azure Marketplace purchase
        │
        ▼
Customer portal sign-in (Entra ID / OIDC)
        │
        ▼
SELF-SERVICE ACTIVATION  (customer clicks Activate; no
   operator. Auto-provisioning enabled = activates 24/7)
   Fulfillment API: PendingFulfillmentStart → Subscribed
        │
        ▼
┌──────────────────────────────────────────────┐
│  SETUP WIZARD  (the five custom steps)        │
│   1. Subscription active   (auto)             │
│   2. Select Azure region   (data residency)   │
│   3. Grant enterprise app permissions (consent)│
│   4. Grant site permissions + set role        │
│   5. Grant Teams activity consent (2nd consent)│
└──────────────────────────────────────────────┘
        │  (driven by customer portal)
        ▼
┌──────────────────────────────────────────────┐
│  appaddin2  (verifier — NOT a driver)         │
│   On first use:  verify steps complete        │
│                  → then create the database    │
│   On access:     verify steps complete        │
│                  → then allow app access       │
└──────────────────────────────────────────────┘
        │
        ▼
Operational against the customer's selected SharePoint sites
```

---

## 2. Purchase & activation (context for the custom steps)

1. **Purchase** — Customer finds the Read & Understood offer in the Azure Portal / Marketplace and subscribes. Azure creates the subscription in state `PendingFulfillmentStart` and redirects the customer to our **landing page** with a marketplace token.
2. **Sign-in** — Customer authenticates to the customer portal with their Entra ID (OpenID Connect). The accelerator resolves the marketplace token against the **Fulfillment API v2** and persists the subscription locally.
   - *Note:* the Fulfillment API requires **lowercase** subscription GUIDs; the local DB stores them uppercase.
3. **Self-service activation (no operator gate).** A new subscription lands in `PendingFulfillmentStart`. With **automatic provisioning enabled** (`IsAutomaticProvisioningSupported = true`), the customer's own **Activate** click calls the Fulfillment API and transitions the subscription `PendingFulfillmentStart → Subscribed` immediately — **no publisher operator is involved**, so activation works 24/7 regardless of our timezone.
   - *Why we run it this way:* most customers start on the **$0 30-day trial**, so there is no billing risk in activating without a human checkpoint; for paid plans the **72-hour cancellation window** plus `appaddin2`'s five-step verification provide the safety net. (A manual admin-portal activation path still exists in the AdminSite but is **not** used in the default self-service flow.)
   - With `RedirectActivateToSetup = true`, the moment the customer activates they are routed straight into the setup wizard (`/Setup/{subscriptionId}`); otherwise they reach it via the **Continue setup** link.
   - **Activation only sets billing/subscription state — it does not unlock the product.** Real access is still gated by `appaddin2` verifying all five custom steps below.

The customer's subscriptions list then shows a **setup progress pill** ("Setup: X of 5" / "Setup: complete") and a **Continue setup** link so the wizard can be resumed at any time.

---

## 3. The five custom setup steps

The wizard is a gated checklist: each step unlocks the next. Progress and completion are tracked per subscription so the flow is fully resumable. The gating is *mostly* linear (1→2→3→4), with one branch: **Step 5 (Teams consent) is gated on Step 3, not Step 4** — Steps 4 and 5 both hang off Step 3 and can be completed in either order. The **"How it completes"** condition stated for each step below is exactly the signal `appaddin2` must check — all must be satisfied for a subscription before `appaddin2` treats setup as complete (Section 4).

### Step 1 — Subscription active *(reflects self-service activation)*

- **What the user sees:** A confirmation card that the subscription is active. Activation is the customer's own **Activate** click (Section 2, step 3) — no operator.
- **How it completes:** Satisfied once the subscription is `Subscribed`.
- **Gates:** Unlocks Step 2.

### Step 2 — Select Azure region (data residency & processing)

- **Why:** Read & Understood must process and store the customer's data in a region of their choosing for data-residency compliance. This selection drives where the runtime processes their tenant's data.
- **Function1 is authoritative.** On **every** Setup load the portal calls the AzRSvc **Function1** detection service for the tenant; it never short-circuits on the region stored in the AMP DB. The stored `AzureRegion` is only a non-authoritative cache of "what was detected or picked". If Function1 disagrees with the stored value, the discrepancy is **logged** and the AMP DB is overwritten with Function1's answer.
- **Two paths, depending on whether Function1 can identify the region:**
  - **Detected** (Function1 returns a region) → it is persisted and the step is marked **complete immediately** — no confirmation, no synchronous endpoint call. A detected region that's stored-but-incomplete (e.g. an older row) is **self-healed** to complete on the next load. Propagation of the regional row is handled by the **daily ratification** (below).
  - **Manually selected** (Function1 can't identify it → picker) → the region is brand new to the regions, so on submit the portal **synchronously calls the fan-out (signaling) endpoint and waits**. Only a confirmed delivery marks the step complete; a failed push leaves the region saved and asks the customer to retry.
- **Propagation = the external daily ratification, not an in-project push.** The regional `tenantregions` rows are created/updated by the **separate Legeris daily `SaaSInitialiseTenantRegions` reconcile job**, which *pulls* a snapshot from the AdminSite **`ReconcileController`** (`GET /api/saasaccelerator/reconcile-snapshot`, HMAC-signed). That snapshot returns every subscription whose `AzureRegion` is set — so **persisting the region here is all this project owes**; the daily job propagates it. (The detected path relies entirely on this; the manually-selected path additionally does an immediate push so a brand-new region exists without waiting for the daily cycle.)
- **Provider-routing record (key signal for `appaddin2`):** the regional `tenantregions` row carries **`SubscriptionProvider = MarketplaceSaaS`**. This is the **trigger for `appaddin2` to bypass the ZoHo Billing flows**. A tenant without this row (or with a non-`MarketplaceSaaS` provider) continues down the existing ZoHo path.
- **How it completes:** Region recorded **AND** fan-out marked complete (immediate for detected; on confirmed push for selected).
- **Gates:** Unlocks Step 3. (Region must be chosen before consent so the consent/runtime app operate in the correct region.)

> **Note:** The new Marketplace-SaaS-routed flows in `appaddin2` (what it does once `SubscriptionProvider = MarketplaceSaaS` is detected) will be built out in **phases under a separate brief**. This document only establishes that the `tenantregions` row with `SubscriptionProvider = MarketplaceSaaS` is the routing trigger.

### Step 3 — Grant enterprise application permissions (admin consent)

- **Why:** The Read & Understood **runtime enterprise application** needs tenant-wide admin consent in the customer's directory before it can be granted access to individual SharePoint sites. This is the standard Entra **admin-consent** flow.
- **What the user sees:** A "Grant tenant consent" action. This is an **admin-only** operation — the person completing it must be a Global Administrator (or privileged-role admin) in the customer tenant.
- **What happens:**
  - The customer is redirected to Microsoft's `/adminconsent` endpoint for the **runtime app client id** (this is the runtime app, **not** the portal sign-in app — see [Entra app architecture](../README.md)).
  - Requested permissions include basic user read and the Microsoft Graph scope needed to later grant per-site access (e.g. `Sites.FullControl.All` / `Sites.Selected` administration).
  - Microsoft redirects back to our consent callback. We validate a tamper-proof signed state token (HMAC, time-limited) and record who consented, their object id, and when.
- **How it completes:** Admin consent recorded for the runtime app.
- **Gates:** Unlocks Step 4.

### Step 4 — Grant SharePoint site permissions (`Sites.Selected`) and set role

This step uses the **`Sites.Selected`** model so Read & Understood only ever has access to the **specific sites the customer enrols** — never the whole tenant. The customer adds one or more sites, grants the runtime app access to each, and then **tightens the role from "manage" to "read"**.

**4a. Add a site**
- Customer enters a SharePoint site URL.
- We validate the site exists via Microsoft Graph and resolve its Graph site id. Invalid/non-existent URLs are rejected with no record created. Duplicate enrolments are refused.
- The site is recorded as **Pending**.

**4b. Grant access (role = "manage" / write)**
- Customer grants the runtime app access to the enrolled site. A `Sites.Selected` permission is created (or updated) for the **runtime app's** identity at role **"write" (manage)** via Graph.
- On success the site is marked **Granted** with role **manage**; on Graph failure the row records the failure reason and stays actionable for retry.

**4c. Alter site permission from "manage" to "read"**
- Once Read & Understood has performed any one-time write operations that require the elevated **manage/write** role (e.g. enabling/configuring the library on the site), the customer **downgrades the role to "read"**.
- This is a Graph PATCH of the existing per-site permission from `write` → `read`, leaving Read & Understood with least-privilege **read-only** access going forward.
- The action is **reversible** (the customer can re-elevate to manage if a future write operation is required), and the UI warns that downgrading to read restricts further library-enable operations.

- **How Step 4 completes:** All enrolled sites are in **Granted** status. (The intended steady state is **read**, with manage used only transiently for initial provisioning.)

> **Permission model summary for `appaddin2`:** access is per-site, granted to the **runtime app identity** under `Sites.Selected`, starting at **write/manage** for setup and ending at **read** for steady-state operation. `appaddin2` must function with read-only access once setup is complete and only expect manage when an explicit write operation is needed. **`appaddin2` does not grant these permissions — the setup wizard does — but it must verify at least one site is enrolled and Granted before allowing access.**

### Step 5 — Grant Send Teams Activities consent (a second admin consent)

- **Why:** Read & Understood posts acknowledgement **activity notifications** into Microsoft Teams. Sending those requires a **second tenant-wide admin consent** — this time for the shared **Acknowledge Teams application** (the app backing the AppSource Teams package's `webApplicationInfo`, which needs the `TeamsActivity.Send` permission). This is a **different Entra app from the Step 3 runtime app**, and is **one shared multi-tenant app across the ZoHo and Marketplace-SaaS products**.
- **This step applies to every install — it is not a Teams-only opt-in.** The AppSource Teams package that this consent backs is also what delivers the **SPFx web part**, so all installs go through Teams. There is no configuration where a tenant completes setup without it.
- **What the user sees:** A "Grant Teams consent" action. Like Step 3 this is **admin-only** — the person completing it must be a Global Administrator (or privileged-role admin) in the customer tenant.
- **What happens:** Same signed-state `/adminconsent` choreography as Step 3, but targeting the **Teams activity app client id** (`TeamsActivityAppClientId` config) instead of the runtime app. Microsoft redirects back to our callback (`/api/setup/teams-consent-callback`); we validate the tamper-proof HMAC, time-limited state token and record who consented, their object id, and when (`TeamsActivityAppConsentedUtc` / `TeamsActivityConsentedByUpn` / `TeamsActivityConsentedByObjectId` on `SubscriptionTenantConsent`). If the Teams activity app id is not configured on the Accelerator, the step surfaces a "not configured" error rather than redirecting.
- **Prerequisite / gating:** Unlocked once **Step 3 runtime consent is recorded** (it needs the tenant known and tenant trust established). It does **not** depend on Step 4 sites — Steps 4 and 5 both hang off Step 3 and may be completed in either order.
- **How it completes:** Teams-activity-app admin consent recorded. This step is **mandatory for every install** — the Teams package delivers both the acknowledgement notifications and the SPFx web part — so a subscription only reaches "Setup: complete" (`5 of 5`) once this consent is granted.
- **Revocation:** If the consent is later removed/revoked in Microsoft Entra, Teams acknowledgement notifications stop; the step exposes a **Re-grant Teams consent** link so the customer can re-establish it.

---

## 4. What `appaddin2` must verify (and when)

`appaddin2` is the **verification gate**. It does not perform any of the setup steps; it reads the state they produce and refuses to proceed until they are complete for the subscription/tenant in question.

**Provider routing (precondition).** Before any of the completion checks apply, `appaddin2` keys off the **`tenantregions`** row written in Step 2: if **`SubscriptionProvider = MarketplaceSaaS`**, the tenant is routed down the new Marketplace SaaS flows and **bypasses the ZoHo Billing flows**. Absence of this row (or a non-`MarketplaceSaaS` provider) means the tenant stays on the existing ZoHo path. *(The detailed Marketplace-SaaS flows are a separate, phased brief.)*

**The five completion signals `appaddin2` checks** (per subscription):

1. **Subscription active** — subscription is `Subscribed` (reached via the customer's self-service Activate click; no operator gate).
2. **Region selected** — a region is recorded **and** the regional fan-out is complete; the `tenantregions` row exists with `SubscriptionProvider = MarketplaceSaaS`.
3. **Admin consent granted** — the runtime enterprise app has tenant admin consent recorded.
4. **Site(s) granted** — at least one SharePoint site is enrolled and in **Granted** status under `Sites.Selected`.
5. **Teams activity consent granted** — the shared Acknowledge Teams app (`TeamsActivity.Send`) has tenant admin consent recorded (`TeamsActivityAppConsentedUtc` set). This applies to **every** subscription (the Teams package also delivers the SPFx web part), so `appaddin2` treats it as a hard gate on the same footing as the other four — not an optional, Teams-only add-on.

**Two verification points (timing-dependent):**

- **On first use — before creating the tenant's database.** The first time a tenant reaches `appaddin2`, it must confirm all five signals **before** provisioning/creating the database. If any are incomplete, it must **not** create the database and should direct the user back to the setup wizard to finish the outstanding step(s).
- **On app access — before allowing access.** On subsequent access (database already exists), `appaddin2` must still confirm the five signals before allowing the app to operate, so that a regression (e.g. consent revoked, site permission downgraded/removed, region not finalised) is caught rather than silently failing.

> The exact point at which verification runs depends on timing — first-use provisioning vs. ongoing access. Both paths gate on the **same five signals**; only the consequence differs (create-database vs. allow-access).

### Cross-cutting behaviours `appaddin2` must respect

- **Gating & resumability** — Steps unlock mostly in order (1→2→3→4), with Step 5 (Teams consent) branching off Step 3 alongside Step 4, and the wizard is resumable. `appaddin2` must tolerate a partially-complete subscription gracefully (send the user back to setup, don't error hard).
- **Region before consent before sites/Teams** — The order matters: region drives processing location; the Step 3 runtime consent enables both the per-site `Sites.Selected` grants (Step 4) and the separate Teams activity consent (Step 5). A later signal cannot be trusted complete if an earlier one regressed.
- **Least privilege end state** — The target per-site role is **read**. `appaddin2` must operate read-only in steady state and treat manage as transitional only.
- **Don't assume — re-check** — Because permissions/consent can be revoked after first use, `appaddin2` re-verifies on access rather than caching a one-time "setup complete" flag indefinitely.

---

## 5. Quick reference — what each step establishes

| Step | User action | Result / what it grants | Completion condition |
|------|-------------|-------------------------|----------------------|
| 1. Active | customer clicks Activate (self-service, no operator) | Subscription activated; wizard unlocked | Subscription `Subscribed` |
| 2. Region | Pick Azure region | Data residency/processing region set; regional fan-out triggered | Region saved **and** fan-out complete |
| 3. Consent | Admin grants tenant consent | Runtime enterprise app consented in customer tenant | Admin consent recorded |
| 4. Sites | Add site → grant → downgrade to read | Per-site `Sites.Selected` access (manage → read) for the runtime app | All enrolled sites Granted (target role: read) |
| 5. Teams consent | Admin grants Send Teams Activities consent (branches off Step 3) | Shared Acknowledge Teams app consented (`TeamsActivity.Send`) so activity notifications can be sent | Teams-activity-app admin consent recorded |

---

*Implementation references (customer portal):* setup wizard lives under `src/CustomerSite` (`SetupController` — including the `TeamsActivity` action and `/api/setup/teams-consent-callback`, `Views/Setup/*` including `_Step5TeamsActivity.cshtml`, `setup.js`); supporting services under `src/Services/Services` (`AzureRegionService`, `TenantAdminConsentService` — `BuildTeamsActivityConsentUrl` / `RecordTeamsActivityConsentAsync`, `SitePermissionService`, `SetupStatusService`); persistence in `src/DataAccess` (`SubscriptionTenantConsent` — Teams consent columns added by migration `AddTeamsActivityConsent`, `SubscriptionSite`). The Teams activity app id is configured via `SaaSApiConfiguration:TeamsActivityAppClientId`. Use these as the behavioural reference when aligning `appaddin2`.

*Activation config (enables the self-service flow in Section 2):*
- **`IsAutomaticProvisioningSupported = true`** — `ApplicationConfiguration` DB table (seed default in `DataAccess/Migrations/Custom/BaselineV2_Seed.cs`). Routes the customer's Activate click straight to the Fulfillment Activate call (`PendingActivationStatusHandler`) with no operator.
- **`SaaSApiConfiguration__RedirectActivateToSetup = true`** — CustomerSite App Service environment variable. Sends the customer into the setup wizard immediately after activation.
