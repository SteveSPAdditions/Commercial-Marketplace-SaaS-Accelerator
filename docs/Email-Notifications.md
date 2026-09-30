# Email Notifications — What Is Sent, When, and Which Options Control It

This document catalogues every outbound email this codebase can send, the code path that
triggers it, the configuration switches that gate it, where the content and recipients come
from, and the admin-portal options that shape each of those.

There is exactly **one** email transport: `SMTPEmailService`, built on `System.Net.Mail.SmtpClient`.
Nothing else in the solution sends mail — no Graph `sendMail`, no SendGrid, no queue-based
mailer. `WebNotificationService` sounds like a mailer but is an HTTPS webhook push, not email
(see §8).

Every setting the mailer uses lives in the **`ApplicationConfiguration` table**, not in
`appsettings.json`. All of it is edited from the publisher portal (AdminSite) under
**Application Config**. Key Vault references therefore do not apply to SMTP credentials.

---

## 1. The two email families

| Family | Triggered by | Emitting code | Templates (`EmailTemplate.Status`) |
|---|---|---|---|
| **Subscription lifecycle** | A customer or admin acting on a subscription, or a Marketplace webhook | `NotificationStatusHandler.Process()` | `Subscribed`, `PendingActivation`, `Unsubscribed`, `Failed` |
| **Metered scheduler** | The `MeteredTriggerJob` console app on its schedule | `MeteredPlanSchedulerManagementService.SendSchedulerEmail()` | `Accepted`, `Failure`, `Missing` |
| **Terms acceptance** | The customer accepting the Marketplace terms + publisher amendment in Setup | `TermsAcceptanceService.Record()` | `TermsAccepted` |

> **In this deployment the metered family is dormant.** Metered billing is emitted by the
> RauMetering function apps via the `UsageLedger` table, and the `MeteredTriggerJob` WebJob has
> been retired (its publish steps were removed from `Publish.ps1`, `Upgrade.ps1` and
> `Deploy.ps1`). The code and config rows still exist and are documented in §3 for completeness,
> but no scheduler email will be sent unless that job is deliberately brought back.

Both families end up in the same place:

```
EmailHelper.PrepareEmailContent / PrepareMeteredEmailContent   → builds EmailContentModel
        ↓
IEmailService.SendEmail  →  SMTPEmailService  →  SmtpClient.Send()
```

---

## 2. Subscription lifecycle emails

### 2.1 Where the trigger is called from

`NotificationStatusHandler.Process(subscriptionId)` is invoked from five places:

| # | Call site | Trigger |
|---|---|---|
| 1 | [HomeController.cs:425](../src/CustomerSite/Controllers/HomeController.cs#L425) (`AutoActivateSubscriptionAsync`) | Customer lands on the portal via a Marketplace token and self-service auto-activation runs |
| 2 | [HomeController.cs:336](../src/CustomerSite/Controllers/HomeController.cs#L336) (landing-page `Index`, repeat-free-trial branch) | A purchaser who already had a free trial arrives with a new free-trial subscription; it is parked in `PendingActivation` and the publisher is notified |
| 3 | [HomeController.cs:863](../src/CustomerSite/Controllers/HomeController.cs#L863) (`SubscriptionOperationAsync`) | Customer clicks **Activate** or **Deactivate** in the customer portal |
| 4 | [HomeController.cs:547](../src/AdminSite/Controllers/HomeController.cs#L547) (`SubscriptionOperation`) | Publisher admin clicks **Activate** or **Deactivate** in the admin portal |
| 5 | [WebhookHandler.cs:402](../src/CustomerSite/WebHook/WebhookHandler.cs#L402) (`UnsubscribedAsync`) | Microsoft sends an **Unsubscribed** webhook (customer cancelled from Azure / M365 admin center) |

Note what is **not** on that list. These webhook operations update the DB and audit log but
send **no** email: `ChangePlanAsync`, `ChangeQuantityAsync`, `ReinstatedAsync`, `RenewedAsync`,
`SuspendedAsync`, `UnknownActionAsync`. Suspension and reinstatement in particular are silent.

Call sites 1 and 2 are wrapped in `try/catch` blocks that deliberately swallow failures —
unconfigured SMTP or missing seed data must not blank the customer's landing page. The
subscription is already activated (or parked) by the time the email step runs.

### 2.2 What the handler decides

`NotificationStatusHandler.Process` reads the subscription's **current** status and derives two
values ([NotificationStatusHandler.cs:143-155](../src/Services/StatusHandlers/NotificationStatusHandler.cs#L143-L155)):

- `planEventName` — `"Unsubscribe"` if status is `Unsubscribed` or `UnsubscribeFailed`, else `"Activate"`
- `processStatus` — `"failure"` if status is `ActivationFailed` or `UnsubscribeFailed`, else `"success"`

Then it gates on three `ApplicationConfiguration` rows
([NotificationStatusHandler.cs:157-171](../src/Services/StatusHandlers/NotificationStatusHandler.cs#L157-L171)):

| Scenario | Config key | Seeded default | Sends when |
|---|---|---|---|
| Pending activation | `IsEmailEnabledForPendingActivation` | `false` | `planEventName == Activate` **and** status **is** `PendingActivation` |
| Activation | `IsEmailEnabledForSubscriptionActivation` | `true` | `planEventName == Activate` **and** status is **not** `PendingActivation` |
| Unsubscription | `IsEmailEnabledForUnsubscription` | `true` | `planEventName == Unsubscribe` |

So with stock defaults: activation and unsubscription mail goes out; the "awaiting your action"
pending-activation mail does not. **Because repeat free trials are parked in `PendingActivation`
(call site 2), `IsEmailEnabledForPendingActivation` must be `true` for the publisher to be told
about them by email.**

### 2.3 Resulting emails

| Subscription status when `Process` runs | Template row used | Seeded subject | Body "welcome text" (from the stored proc) |
|---|---|---|---|
| `PendingActivation` | `PendingActivation` | *Pending Activation* | "A request for purchase with the following details is awaiting your action for activation." |
| `Subscribed` | `Subscribed` | *Subscribed* | "Your request for the purchase has been approved." |
| `Unsubscribed` | `Unsubscribed` | *Unsubscribed* | "A subscription with the following details was deleted from Azure." |
| `ActivationFailed` / `UnsubscribeFailed` | `Failed` | *Failed* | "Your request for the subscription has been failed." |

### 2.4 Recipients

Resolved in `EmailHelper.PrepareEmailContent`
([EmailHelper.cs:55-120](../src/Services/Helpers/EmailHelper.cs#L55-L120)), in this order:

1. **Base recipients** come from the `EmailTemplate` row matching the status —
   `ToRecipients`, `Cc`, `Bcc`, `Subject`. Edited in the admin portal at
   **Application Config → View Templates → Edit**.
2. **Plan-level override**: if a `PlanEventsMapping` row exists for
   (`PlanGuid`, event `Activate` or `Unsubscribe`) and its `SuccessStateEmails` is non-empty, that
   value **replaces** the `To` list entirely. Edited on the admin **Plans → Plan Details → Events**
   tab ([PlanDetails.cshtml:137](../src/AdminSite/Views/Plans/PlanDetails.cshtml#L137)).
3. **Copy to customer**: if the same mapping row has `CopyToCustomer = true`, a **second,
   separate** email is sent with `To` set to the subscription owner's address
   ([NotificationStatusHandler.cs:178-182](../src/Services/StatusHandlers/NotificationStatusHandler.cs#L178-L182)).
   The customer is not simply CC'd — they get their own send of identical content, and the
   BCC list from the template goes out again with it.

Addresses are semicolon-delimited (`;`), trimmed, and blank entries are skipped.

If both `To` and `Bcc` end up empty, nothing is sent and a row is written to `ApplicationLog`
explaining why ([SMTPEmailService.cs:110](../src/Services/Services/SMTPEmailService.cs#L110)).

#### Plan Events tab: which fields actually do something

The Events tab shows three rows per plan (`Activate`, `Unsubscribe`, `Pending Activation`) with
**Active**, **Copy To Customer**, **Success Event Emails** and **Failure Event Emails**. The email
code only reads some of that:

| Field | Effect on email |
|---|---|
| Success Event Emails | Replaces the template `To` list for that plan when non-empty. **Used.** |
| Copy To Customer | Triggers the second send to the subscription owner. **Used.** |
| Active checkbox | Saved to `PlanEventsMapping.Isactive` but **never checked** by `EmailHelper` — an inactive row with addresses still overrides `To`. |
| Failure Event Emails | Saved to `PlanEventsMapping.FailureStateEmails` but **never read**. Failure emails use the same `Success Event Emails` / template recipients as success. |
| `Pending Activation` row | Hidden when `IsAutomaticProvisioningSupported` is `true` ([PlansRepository.cs:287-294](../src/DataAccess/Services/PlansRepository.cs#L287-L294)). Even when shown, its addresses are **never consulted** — the handler only looks up the `Activate` and `Unsubscribe` events, so pending-activation mail uses the `Activate` row's override. |

Similarly, the **IsActive** checkbox on the Email Template edit page is saved but not checked
before sending. Turn a scenario off with the `IsEmailEnabledFor*` config flags, not the
template checkbox.

### 2.5 Body composition

The body is **not** built in C#. `EmailTemplateRepository.GetEmailBodyForSubscription` calls the
stored procedure `dbo.spGetFormattedEmailBody(@subscriptionId, @processStatus)`, defined in
[BaselineV2_Seed.cs:157](../src/DataAccess/Migrations/Custom/BaselineV2_Seed.cs#L157).

The proc:
1. Selects `EmailTemplate.TemplateBody` for the matching status (`Failed` when
   `@processStatus = 'failure'`, otherwise the subscription's own status).
2. Builds an HTML table of subscription facts: Customer Email Address, Customer Name, SaaS
   Subscription Id, SaaS Subscription Name, SaaS Subscription Status, Plan, Purchaser Email
   Address, Purchaser Tenant — plus every enabled offer/plan attribute value for that subscription.
   (Quirk: the "Purchaser Email Address" row is populated from the *customer* email variable,
   not `Subscriptions.PurchaserEmail`, so the two email rows always show the same value.)
3. Substitutes three placeholders in the template body:
   - `${subscriptiondetails}` → the table above
   - `${welcometext}` → the status-specific sentence from §2.3
   - `${ApplicationName}` → the `ApplicationName` config value (seeded as `Contoso`)

The seeded lifecycle templates are full HTML documents with a header image pointing at the
upstream Contoso logo on GitHub. Replace them from the Email Templates page (the page
recommends editing the body in an external editor and pasting it back).

**Gotcha:** a status with no `EmailTemplate` row (e.g. `PendingUnsubscribe`) yields a NULL body,
so the email sends with empty content. Only the four statuses in §2.3 are seeded.

---

### 2.6 Terms-acceptance confirmation

Sent by `TermsAcceptanceService.Record()` (`src/Services/Services/TermsAcceptanceService.cs`) the
moment a customer ticks both boxes on the Setup terms gate and submits. It is **not** routed through
`NotificationStatusHandler` or the Plan Events tab.

- **Gate:** `IsEmailEnabledForTermsAcceptance` (default `true`) **and** an active `TermsAccepted`
  template with a non-empty body. Missing either = no send, a warning in the application log, and the
  acceptance is still recorded — the email is confirmation, never the record.
- **Recipients:** **To** is always the accepter's UPN. **CC/BCC** come from the template row, so set
  **Bcc** on the `TermsAccepted` template to keep a publisher copy. `ToRecipients` on the template is ignored.
- **Body:** the template body with these placeholders replaced — `****SubscriptionName****`,
  `****SubscriptionId****`, `****AcceptedBy****`, `****AcceptedUtc****`, `****MicrosoftContractUrl****`,
  `****MicrosoftContractVersion****`, `****PublisherAmendmentTitle****`, `****PublisherAmendmentUrl****`,
  `****PublisherAmendmentVersion****`. A site-relative amendment path (e.g. `/legal/amendment-v1.html`)
  is made absolute with `SaaSApiConfiguration:CustomerSiteBaseUrl` so the link works from a mail client.
- **Subject:** the template subject, with `****SubscriptionName****` replaced.
- **Transport:** the same six `SMTP*` rows as every other email; failures are logged and swallowed.

## 3. Metered scheduler emails (dormant here — see §1)

Sent by the **`MeteredTriggerJob`** console app, not by either web site. All of it is gated
behind `IsMeteredBillingEnabled`; if that is false the job exits without doing anything
([MeteredTriggerHelper.cs:116](../src/MeteredTriggerJob/MeteredTriggerHelper.cs#L116)).

| Scenario | Where triggered | Config gate (seeded default) | Template | Seeded subject |
|---|---|---|---|---|
| Usage event accepted by the Metering API (`StatusCode == "Accepted"`) | `UpdateSchedulerItem` → `SendSchedulerEmail` | `EnablesSuccessfulSchedulerEmail` (`False`) | `Accepted` | *Scheduled SaaS Metered Usage Submitted Successfully!* |
| Usage event rejected/errored (any other status code) | same | `EnablesFailureSchedulerEmail` (`False`) | `Failure` | *Scheduled SaaS Metered Usage Failure!* |
| A scheduled item's run time has **passed** and it never ran | `Execute` → `SendMissingEmail` | `EnablesMissingSchedulerEmail` (`False`) | `Missing` | *Scheduled SaaS Metered Task was Skipped!* |

Details:

- **Missing** mail is suppressed if `CheckIfSchedulerRun` shows the task already ran once
  ([MeteredTriggerHelper.cs:355-358](../src/MeteredTriggerJob/MeteredTriggerHelper.cs#L355-L358)) —
  it fires once per genuinely-skipped schedule, not on every job pass.
- Recipients come **only** from the `SchedulerEmailTo` application-config value (seeded empty).
  If it is empty, `PrepareMeteredEmailContent` **throws**
  ([EmailHelper.cs:129-132](../src/Services/Helpers/EmailHelper.cs#L129-L132)); the exception is
  caught and logged by the job, so a metered run is not lost — but no mail goes out.
  Plan-level `SuccessStateEmails` / `CopyToCustomer` do **not** apply to this family.
- Body placeholders are substituted directly in C#, not via a stored proc
  ([EmailHelper.cs:134](../src/Services/Helpers/EmailHelper.cs#L134)):
  `****SubscriptionName****`, `****SchedulerTaskName****`, `****ResponseJson****`.

**Behavioural quirk worth knowing:** the gate check is an `OR` —
`if (enablesFailureSchedulerEmail || enablesSuccessfulSchedulerEmail)`
([MeteredTriggerHelper.cs:296](../src/MeteredTriggerJob/MeteredTriggerHelper.cs#L296)) — and
`SendSchedulerEmail` then picks the template purely from the status code. Turning on *either*
flag turns on *both* success and failure mail.

---

## 4. SMTP configuration

Every connection setting is read from the `ApplicationConfiguration` table per send, in
`EmailHelper.FinalizeContentEmail`
([EmailHelper.cs:137-153](../src/Services/Helpers/EmailHelper.cs#L137-L153)):

| Config key | Purpose | Seeded default |
|---|---|---|
| `SMTPHost` | Server hostname | *(empty)* |
| `SMTPPort` | Port; falls back to `0` if unparseable | *(empty)* |
| `SMTPUserName` | Auth username | *(empty)* |
| `SMTPPassword` | Auth password (basic auth only; no OAuth path) | *(empty)* |
| `SMTPSslEnabled` | `EnableSsl`; falls back to `false` if unparseable | *(empty)* |
| `SMTPFromEmail` | `From` address | *(empty)* |
| `ApplicationName` | `${ApplicationName}` placeholder in bodies | `Contoso` |

Edited in the admin portal under **Application Config → Edit**. The Application Config list
masks `SMTPPassword` after its first five characters for display only; the value is stored in
plaintext in SQL. All mail is sent as HTML (`IsBodyHtml = true`).

The transport only supports username/password SMTP AUTH. Provider selection (Mandrill vs an
Exchange shared mailbox) is discussed in
[SMTP-Provider-Choice-Mandrill-vs-Exchange.md](SMTP-Provider-Choice-Mandrill-vs-Exchange.md).

### Failure handling

`SMTPEmailService.SendEmail` never throws. `SmtpException` and general exceptions are both
caught and written to `ApplicationLog`
([SMTPEmailService.cs:97-106](../src/Services/Services/SMTPEmailService.cs#L97-L106)). A failed
send is therefore **silent** to the user and to the calling flow — the only evidence is a row in
`ApplicationLog` reading `"<subject>: SMTP exception <message>."`. Successful sends log
`"<subject>: Email sent succesfully!"` (sic). View these in the admin portal under
**Application Log**.

There is no retry and no dead-letter for email.

### Known defect: CC

`emailContent.CCEmails` is populated by `EmailHelper` from the template's `Cc` column, but
`SMTPEmailService.SendEmail` never applies it to the `MailMessage` — **CC is silently dropped**.
Anything entered in the Email Template **Cc** field has no effect. Use **Bcc** or add the
address to **ToRecipients**.

BCC previously had the same class of bug (the loop iterated the *To* list, so BCC addresses were
never used and To recipients were duplicated instead). That is fixed — `To` and `Bcc` are now each
split from their own field ([SMTPEmailService.cs:70-90](../src/Services/Services/SMTPEmailService.cs#L70-L90)).
A BCC-only send (empty `To`) works.

---

## 5. Every email-related option, in one place

### 5.1 `ApplicationConfiguration` rows (admin portal → Application Config)

| Key | Family | Default | What it does |
|---|---|---|---|
| `IsEmailEnabledForSubscriptionActivation` | Lifecycle | `true` | Send on activation success/failure (`Subscribed`, `ActivationFailed`) |
| `IsEmailEnabledForUnsubscription` | Lifecycle | `true` | Send on unsubscribe success/failure (`Unsubscribed`, `UnsubscribeFailed`) |
| `IsEmailEnabledForPendingActivation` | Lifecycle | `false` | Send when a subscription lands in `PendingActivation` (manual-approval flows and parked repeat free trials) |
| `IsEmailEnabledForTermsAcceptance` | Terms | `true` | Send the customer a confirmation when they accept the Marketplace terms + amendment in Setup (see §2.6) |
| `SMTPHost` / `SMTPPort` / `SMTPUserName` / `SMTPPassword` / `SMTPSslEnabled` / `SMTPFromEmail` | Both | *(empty)* | SMTP connection and sender |
| `ApplicationName` | Lifecycle | `Contoso` | `${ApplicationName}` token in template bodies |
| `SchedulerEmailTo` | Metered | *(empty)* | Sole recipient list for scheduler mail; empty = throw + no send |
| `EnablesSuccessfulSchedulerEmail` | Metered | `False` | Scheduler success mail (also enables failure mail — see §3) |
| `EnablesFailureSchedulerEmail` | Metered | `False` | Scheduler failure mail (also enables success mail — see §3) |
| `EnablesMissingSchedulerEmail` | Metered | `False` | Scheduler "skipped window" mail |
| `IsMeteredBillingEnabled` | Metered | `False` here | Master switch for the retired job; leave off (double-billing hazard) |
| `IsAutomaticProvisioningSupported` | Lifecycle (indirect) | `true` here | Hides the `Pending Activation` row on the Plan Events tab; does not gate any email |

### 5.2 Email Templates page (Application Config → View Templates → Edit)

One row per `EmailTemplate.Status`: `Failed`, `PendingActivation`, `Subscribed`, `Unsubscribed`,
`Accepted`, `Failure`, `Missing`.

| Field | Effect |
|---|---|
| Subject | Used as-is (no token substitution) |
| TemplateBody | HTML; lifecycle templates may use `${subscriptiondetails}`, `${welcometext}`, `${ApplicationName}`; scheduler templates use `****SubscriptionName****`, `****SchedulerTaskName****`, `****ResponseJson****` |
| ToRecipients | Default `To` list (`;`-separated); overridden per plan by Success Event Emails |
| Bcc | Always applied, on every send including the customer copy |
| Cc | **Ignored** (see §4) |
| Description | Display only |
| IsActive | **Ignored** by the sender |

### 5.3 Plan Details → Events tab (per plan)

| Field | Effect |
|---|---|
| Success Event Emails (`Activate` / `Unsubscribe` rows) | Replaces `ToRecipients` for that plan and event |
| Copy To Customer | Sends a second copy to the subscription owner |
| Active | Ignored by the sender |
| Failure Event Emails | Ignored by the sender |
| `Pending Activation` row | Ignored by the sender |

---

## 6. Quick reference — will an email be sent?

```
Customer auto-activated on portal entry   → yes, if IsEmailEnabledForSubscriptionActivation (best-effort)
Repeat free trial parked for approval     → yes, if IsEmailEnabledForPendingActivation (best-effort)
Customer clicks Activate (portal)         → yes, if IsEmailEnabledForSubscriptionActivation
Admin clicks Activate (admin site)        → yes, same flag
Customer/admin clicks Deactivate          → yes, if IsEmailEnabledForUnsubscription
Customer accepts terms in Setup           → yes, to the accepter, if IsEmailEnabledForTermsAcceptance
Subscription left in PendingActivation    → only if IsEmailEnabledForPendingActivation (default off)
Activation / unsubscribe failed           → yes, "Failed" template, via the same flags
Webhook: Unsubscribed                     → yes, if IsEmailEnabledForUnsubscription
Webhook: ChangePlan / ChangeQuantity /
         Reinstated / Renewed / Suspended  → NO email at all
Metered usage posted OK / failed          → only if MeteredTriggerJob is running (it is retired here)
Metered schedule missed its window        → same
Any of the above with empty To and Bcc    → no send; ApplicationLog row explains why
Any of the above with bad SMTP settings   → no send; ApplicationLog row with the SMTP exception
```

---

## 7. Setting it up (operator checklist)

1. **Application Config**: fill the six `SMTP*` rows and set `ApplicationName`.
2. **Application Config**: set the three `IsEmailEnabledFor*` flags for the scenarios you want.
   Turn `IsEmailEnabledForPendingActivation` on if you want to hear about parked repeat free trials.
3. **View Templates**: for each of `Subscribed`, `Unsubscribed`, `PendingActivation`, `Failed`,
   set **ToRecipients** (and optionally **Bcc**), and replace the Contoso HTML body.
   For `TermsAccepted`, set **Bcc** if you want a publisher copy (To is always the accepter).
4. **Plans → Events** (optional): per-plan **Success Event Emails** and **Copy To Customer**.
5. Trigger a test (activate a test subscription) and check **Application Log** for
   `Email sent succesfully!` or an `SMTP exception` line.

---

## 8. Related, but not outbound email

| Thing | What it actually is |
|---|---|
| `WebNotificationUrl` (Application Config) | HTTPS POST of subscription events to an external endpoint (`WebNotificationService`). Must be `https` on port 443; empty means "not configured". |
| `Branding:SupportEmail` (`CustomerSite/appsettings.json`) | Rendered as a `mailto:` link in the customer-site footer. Nothing is sent by the app. |
| `KnownUsers` app setting / Known Users page | Allow-list of email addresses permitted into the publisher portal. Access control, not mail. |
| `PublisherAdminUsers` (`Deploy.ps1`) | Seeds `KnownUsers` at deployment time. |
| RAU outbox / signaling | Durable HTTP signaling to the RAU tenant; see [Outbox-Signaling-Architecture.md](Outbox-Signaling-Architecture.md). |

---

## 9. Files involved

| File | Role |
|---|---|
| [src/Services/Services/SMTPEmailService.cs](../src/Services/Services/SMTPEmailService.cs) | The only transport; `SmtpClient.Send` |
| [src/Services/Contracts/IEmailService.cs](../src/Services/Contracts/IEmailService.cs) | `SendEmail(EmailContentModel)` |
| [src/Services/Helpers/EmailHelper.cs](../src/Services/Helpers/EmailHelper.cs) | Builds subject/body/recipients/SMTP settings |
| [src/Services/Models/EmailContentModel.cs](../src/Services/Models/EmailContentModel.cs) | The DTO handed to the transport |
| [src/Services/Models/EmailTriggerConfigurationConstants.cs](../src/Services/Models/EmailTriggerConfigurationConstants.cs) | Names of the three `IsEmailEnabledFor*` keys |
| [src/Services/StatusHandlers/NotificationStatusHandler.cs](../src/Services/StatusHandlers/NotificationStatusHandler.cs) | Lifecycle trigger + enable-flag gating |
| [src/Services/Services/MeteredPlanSchedulerManagementService.cs](../src/Services/Services/MeteredPlanSchedulerManagementService.cs) | `SendSchedulerEmail` |
| [src/MeteredTriggerJob/MeteredTriggerHelper.cs](../src/MeteredTriggerJob/MeteredTriggerHelper.cs) | Scheduler run loop; success/failure/missing decisions (retired job) |
| [src/DataAccess/Entities/EmailTemplate.cs](../src/DataAccess/Entities/EmailTemplate.cs) | Template row shape |
| [src/DataAccess/Entities/PlanEventsMapping.cs](../src/DataAccess/Entities/PlanEventsMapping.cs) | Per-plan event recipients |
| [src/DataAccess/Services/EmailTemplateRepository.cs](../src/DataAccess/Services/EmailTemplateRepository.cs) | Template lookup + `spGetFormattedEmailBody` call |
| [src/DataAccess/Migrations/Custom/BaselineV2_Seed.cs](../src/DataAccess/Migrations/Custom/BaselineV2_Seed.cs) | Stored proc, SMTP config seeds, `Events` seed, lifecycle templates |
| [src/DataAccess/Migrations/Custom/BaselineV7_Seed.cs](../src/DataAccess/Migrations/Custom/BaselineV7_Seed.cs) | Scheduler config seeds + `Accepted`/`Failure`/`Missing` templates |
| [src/AdminSite/Controllers/ApplicationConfigController.cs](../src/AdminSite/Controllers/ApplicationConfigController.cs) | Admin UI for editing templates and SMTP config |
| [src/AdminSite/Views/ApplicationConfig/EmailTemplateDetails.cshtml](../src/AdminSite/Views/ApplicationConfig/EmailTemplateDetails.cshtml) | Template edit form |
| [src/AdminSite/Views/Plans/PlanDetails.cshtml](../src/AdminSite/Views/Plans/PlanDetails.cshtml) | Per-plan `SuccessStateEmails` / `CopyToCustomer` |
| [docs/Publisher-Experience.md](Publisher-Experience.md#email-setup) | Upstream setup walkthrough with screenshots |

`IEmailService` is registered as scoped in all three hosts:
[AdminSite/Startup.cs:269](../src/AdminSite/Startup.cs#L269),
[CustomerSite/Startup.cs:301](../src/CustomerSite/Startup.cs#L301),
[MeteredTriggerJob/Program.cs:54](../src/MeteredTriggerJob/Program.cs#L54).
