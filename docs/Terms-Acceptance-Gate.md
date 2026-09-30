# Terms Acceptance Gate — Customer Portal

**Purpose:** Before a customer can action any Setup step, they must confirm they agree to the
**Microsoft Standard Contract for the commercial marketplace** and to **SP Additions' amendment** to it,
and we must keep a record of **who agreed and when**.

**Why:** Both documents already bind the customer at the point of purchase in the Marketplace. But the
amendment is only offered there as an HTML *download* (a known Marketplace limitation with no fix in
sight), so customers routinely never open it. The portal gate is a confirmed re-acknowledgement with an
auditable record, and it hosts the amendment as a readable page.

Added 2026-09-30. Migration `AddSubscriptionTermsAcceptance`.

---

## 1. What the customer sees

A new card in the Setup checklist, directly under **Subscription active** and before the region step:

- Two checkboxes, one per document, each linking to the document (opens in a new tab):
  1. *I have read and agree to the Microsoft Standard Contract for the Microsoft commercial marketplace.*
  2. *I have read and agree to the SP Additions Ltd amendment to the Standard Contract (version 1.0).*
- An **Accept and continue** button, disabled until both are ticked (and re-validated server-side).
- A note that their name, the time and the document versions will be recorded, and a confirmation emailed.

Until accepted, every other step shows **Locked — available after you accept the terms**. Steps that are
already complete (an existing tenant) are never shown as regressed. After acceptance the card shows
*Accepted by &lt;upn&gt; on &lt;date&gt;* with links to the exact document versions accepted.

The subscriptions list pill becomes **Setup: X of 6**.

## 2. Semantics (decisions)

| Question | Decision |
|---|---|
| Where does the gate sit? | In Setup, after activation. Activation only sets billing state; the purchase already formed the contract. |
| One checkbox or two? | **Two**, one per document. |
| Who may accept? | Any user in the subscription's tenant — the same rule as the rest of Setup (the person completing Setup is often not the purchaser). |
| Version bump after acceptance? | **No re-prompt.** One acceptance per subscription, ever. The versions on the row record what was presented. |
| Resubscribe? | **Not carried over.** A new subscription is a new purchase; re-confirming is one click. Consistent with `SetupCarryOverService` ("structure, not state"). |
| Documents not configured? | **Fail closed.** With the gate on and either URL blank, the step shows "not configured, contact support" and Setup stays locked. `TermsAcceptanceRequired=false` is the emergency override. |
| Does `appaddin2` need a sixth signal? | **No.** Steps 2–5 cannot be actioned without the gate, so the existing five signals imply it. |

## 3. Enforcement

- **Server-side.** `SetupController.TermsGateBlocks()` runs right after tenant authorisation in every
  mutating action: `Region`, `Consent`, `TeamsActivity`, `AddSite`, `RemoveSite`, and the three site role
  transitions. A blocked call redirects to the checklist with an error flash.
- **Silent writes too.** The region auto-detect/auto-save that normally runs on checklist load is skipped
  while the gate blocks and no region is stored yet.
- **UI.** `BuildSetupViewModelAsync` evaluates the gate first and downgrades every non-complete step to
  `Locked`. `Status.json` (the poller) gains `termsRequired` / `termsAccepted`.

## 4. What is recorded

Table `SubscriptionTermsAcceptance` — **append-only**, one row per acceptance event:

| Column | Notes |
|---|---|
| `AmpSubscriptionId`, `TenantId` | Subscription and purchaser tenant |
| `AcceptedUtc` | Server time, UTC |
| `AcceptedByUpn`, `AcceptedByObjectId`, `AcceptedByDisplayName` | From the signed-in Entra identity |
| `MicrosoftContractAccepted`, `MicrosoftContractUrl`, `MicrosoftContractVersion` | As presented at the time |
| `PublisherAmendmentAccepted`, `PublisherAmendmentTitle`, `PublisherAmendmentUrl`, `PublisherAmendmentVersion` | As presented at the time |
| `IpAddress` (X-Forwarded-For aware), `UserAgent`, `Source` (`Setup`) | Evidence |

Also written: a `SubscriptionAuditLogs` row with attribute **`TermsAccepted`** (visible in both portals'
subscription log views), an `ApplicationLog` line, and the confirmation email
(see [Email-Notifications.md §2.6](Email-Notifications.md)).

**Where to see it:** Admin portal → Subscriptions → Details → **Marketplace Terms** row (who, when, versions, IP).

## 5. Configuration (`ApplicationConfiguration`, admin portal → Application Config)

| Key | Seeded default | Purpose |
|---|---|---|
| `TermsAcceptanceRequired` | `true` | Kill switch. `false` hides the step and satisfies the gate. A **missing** row counts as `true`. |
| `TermsMicrosoftContractTitle` | Microsoft Standard Contract for the Microsoft commercial marketplace | Display title |
| `TermsMicrosoftContractUrl` | `https://go.microsoft.com/fwlink/?linkid=2041178` | Link the customer reads |
| `TermsMicrosoftContractVersion` | *(empty)* | Optional version label, recorded on acceptance |
| `TermsPublisherAmendmentTitle` | SP Additions Ltd amendment to the Standard Contract | Display title |
| `TermsPublisherAmendmentUrl` | `/legal/amendment-v1.html` | Site-relative path served by the customer portal, or an absolute URL |
| `TermsPublisherAmendmentVersion` | `1.0` | Version label, recorded on acceptance |
| `IsEmailEnabledForTermsAcceptance` | `true` | Confirmation email on/off |

Email template: `EmailTemplate.Status = 'TermsAccepted'` (seeded, active). Set **Bcc** on it for a publisher copy.

## 6. Hosting the amendment

`src/CustomerSite/wwwroot/legal/amendment-v1.html` is the page the customer reads and agrees to. It carries
the **Universal Amendment** text exactly as published on the Read & Understood Compliance Suite offer
(Partner Center > Properties > Legal), version 1.0.

Because the URL and version are recorded against each acceptance, **never edit a published amendment
file in place**. Publish a change as a new file (`amendment-v2.html`), then update
`TermsPublisherAmendmentUrl` and `TermsPublisherAmendmentVersion`. Existing acceptances are unaffected
(see §2: no re-prompt).

## 7. Rollout

1. Apply the migration (creates the table, seeds the eight config rows and the email template).
2. Publish CustomerSite and AdminSite.
3. Confirm the two document URLs in Application Config (the amendment page is already populated, §6).
4. Optionally set **Bcc** on the `TermsAccepted` template.
5. Smoke test: open a test subscription's Setup — steps 2–5 should be locked; tick both boxes; confirm the
   card, the admin **Marketplace Terms** row, the audit log entry and the email.

**Existing subscriptions** have no acceptance row, so their checklist shows the terms card as *Action
needed* and the pill drops to *5 of 6* until someone in the tenant ticks the boxes. Their completed steps
stay complete and nothing they already set up is affected. If a retroactive backfill is preferred for
tenants that pre-date the gate, insert rows manually (`Source = 'Backfill'`).

## 8. Files

| Area | Files |
|---|---|
| Data | `DataAccess/Entities/SubscriptionTermsAcceptance.cs`, `Contracts/ISubscriptionTermsAcceptanceRepository.cs`, `Services/SubscriptionTermsAcceptanceRepository.cs`, `Context/SaasKitContext.cs`, `Migrations/*_AddSubscriptionTermsAcceptance.cs` |
| Services | `Services/Services/TermsAcceptanceService.cs`, `Contracts/ITermsAcceptanceService.cs`, `Models/TermsAcceptanceModels.cs`, `Models/SubscriptionLogAttributes.cs` (`TermsAccepted`), `Services/SetupStatusService.cs`, `Models/SetupStatusSummary.cs` |
| CustomerSite | `Controllers/SetupController.cs` (`TermsGateBlocks`, `Terms` POST), `Models/SetupViewModel.cs`, `Views/Setup/_StepTerms.cshtml` (+ locked hints in `_Step2..5`), `wwwroot/css/setup.css`, `wwwroot/js/setup.js`, `setup-inline.js`, `wwwroot/legal/amendment-v1.html`, `Startup.cs` |
| AdminSite | `Controllers/HomeController.cs`, `Views/Home/SubscriptionDetails.cshtml`, `Startup.cs`; `Services/Models/SubscriptionResultExtension.cs` |
| Tests | `Services.Test/TermsAcceptanceServiceTest.cs` |
