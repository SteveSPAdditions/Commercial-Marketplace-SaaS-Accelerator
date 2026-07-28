# Email Notifications — What Is Sent, When, and What Triggers It

This document catalogues every outbound email this codebase can send, the code path that
triggers it, the config switches that gate it, and where the content comes from.

There is exactly **one** email transport: `SMTPEmailService` (`System.Net.Mail.SmtpClient`).
Nothing else in the solution sends mail — no Graph `sendMail`, no SendGrid, no queue-based
mailer. `WebNotificationService` sounds like a mailer but is an HTTP webhook push, not email.

---

## 1. The two email families

| Family | Triggered by | Emitting code | Templates |
|---|---|---|---|
| **Subscription lifecycle** | A customer or admin acting on a subscription, or a Marketplace webhook | `NotificationStatusHandler.Process()` | `Subscribed`, `PendingActivation`, `Unsubscribed`, `Failed` |
| **Metered scheduler** | The `MeteredTriggerJob` console app on its schedule | `MeteredPlanSchedulerManagementService.SendSchedulerEmail()` | `Accepted`, `Failure`, `Missing` |

Both families end up in the same place:

```
EmailHelper.PrepareEmailContent / PrepareMeteredEmailContent   → builds EmailContentModel
        ↓
IEmailService.SendEmail  →  SMTPEmailService  →  SmtpClient.Send()
```

---

## 2. Subscription lifecycle emails

### 2.1 Where the trigger is called from

`NotificationStatusHandler.Process(subscriptionId)` is invoked from exactly four places:

| # | Call site | Trigger |
|---|---|---|
| 1 | [HomeController.cs:794](../src/CustomerSite/Controllers/HomeController.cs#L794) (`SubscriptionOperationAsync`) | Customer clicks **Activate** or **Deactivate** in the customer portal |
| 2 | [HomeController.cs:389](../src/CustomerSite/Controllers/HomeController.cs#L389) (`AutoActivateSubscriptionAsync`) | Customer lands on the portal via a Marketplace token and self-service auto-activation runs |
| 3 | [HomeController.cs:487](../src/AdminSite/Controllers/HomeController.cs#L487) (`SubscriptionOperation`) | Publisher admin clicks **Activate** or **Deactivate** in the admin portal |
| 4 | [WebhookHandler.cs:380](../src/CustomerSite/WebHook/WebhookHandler.cs#L380) (`UnsubscribedAsync`) | Microsoft sends an **Unsubscribed** webhook (customer cancelled from Azure) |

Note what is **not** on that list. These webhook operations update the DB and audit log but
send **no** email: `ChangePlanAsync`, `ChangeQuantityAsync`, `ReinstatedAsync`, `RenewedAsync`,
`SuspendedAsync`. Suspension and reinstatement in particular are silent.

Call site 2 is wrapped in a `try/catch` that deliberately swallows failures
([HomeController.cs:391-399](../src/CustomerSite/Controllers/HomeController.cs#L391-L399)) —
unconfigured SMTP must not blank the customer's landing page.

### 2.2 What the handler decides

`NotificationStatusHandler.Process` reads the subscription's **current** status and derives two
values ([NotificationStatusHandler.cs:143-155](../src/Services/StatusHandlers/NotificationStatusHandler.cs#L143-L155)):

- `planEventName` — `"Unsubscribe"` if status is `Unsubscribed` or `UnsubscribeFailed`, else `"Activate"`
- `processStatus` — `"failure"` if status is `ActivationFailed` or `UnsubscribeFailed`, else `"success"`

Then it gates on three `ApplicationConfiguration` rows:

| Event | Config key | Seeded default | Sends when |
|---|---|---|---|
| Pending activation | `IsEmailEnabledForPendingActivation` | `false` | `planEventName == Activate` **and** status **is** `PendingActivation` |
| Activation | `IsEmailEnabledForSubscriptionActivation` | `true` | `planEventName == Activate` **and** status is **not** `PendingActivation` |
| Unsubscription | `IsEmailEnabledForUnsubscription` | `true` | `planEventName == Unsubscribe` |

So with stock defaults: activation and unsubscription mail goes out; the "awaiting your action"
pending-activation mail does not.

### 2.3 Resulting emails

| Subscription status when `Process` runs | Template row used | Default subject | Body "welcome text" (from the stored proc) |
|---|---|---|---|
| `PendingActivation` | `PendingActivation` | *Pending Activation* | "A request for purchase with the following details is awaiting your action for activation." |
| `Subscribed` | `Subscribed` | *Subscribed* | "Your request for the purchase has been approved." |
| `Unsubscribed` | `Unsubscribed` | *Unsubscribed* | "A subscription with the following details was deleted from Azure." |
| `ActivationFailed` / `UnsubscribeFailed` | `Failed` | *Failed* | "Your request for the subscription has been failed." |

### 2.4 Recipients

Resolved in `EmailHelper.PrepareEmailContent`
([EmailHelper.cs:55-112](../src/Services/Helpers/EmailHelper.cs#L55-L112)), in this order:

1. **Base recipients** come from the `EmailTemplate` row matching the status —
   `ToRecipients`, `Cc`, `Bcc`, `Subject`. Edited in the admin portal at
   **Application Config → Email Templates** (`ApplicationConfigController.EmailTemplateDetails`).
2. **Plan-level override**: if a `PlanEventsMapping` row exists for
   (`PlanGuid`, event `Activate`/`Unsubscribe`) and its `SuccessStateEmails` is non-empty, that
   value **replaces** the `To` list entirely. Edited on the admin **Plan Details** page
   ([PlanDetails.cshtml:137](../src/AdminSite/Views/Plans/PlanDetails.cshtml#L137)).
3. **Copy to customer**: if the same mapping row has `CopyToCustomer = true`, a **second,
   separate** email is sent with `To` set to the subscription owner's address
   ([NotificationStatusHandler.cs:178-182](../src/Services/StatusHandlers/NotificationStatusHandler.cs#L178-L182)).
   The customer is not simply CC'd — they get their own send of identical content.

Addresses are semicolon-delimited (`;`).

If both `To` and `Bcc` end up empty, nothing is sent and a row is written to `ApplicationLog`
explaining why ([SMTPEmailService.cs:100-103](../src/Services/Services/SMTPEmailService.cs#L100-L103)).

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
3. Substitutes three placeholders in the template body:
   - `${subscriptiondetails}` → the table above
   - `${welcometext}` → the status-specific sentence from §2.3
   - `${ApplicationName}` → the `ApplicationName` config value (seeded as `Contoso`)

**Gotcha:** a status with no `EmailTemplate` row (e.g. `PendingUnsubscribe`) yields a NULL body,
so the email sends with empty content. Only the four statuses in §2.3 are seeded.

---

## 3. Metered scheduler emails

Sent by the **`MeteredTriggerJob`** console app (run on a schedule — WebJob/cron/container job),
not by either web site. All of it is gated behind `IsMeteredBillingEnabled`; if that is false the
job exits without doing anything ([MeteredTriggerHelper.cs:116-117](../src/MeteredTriggerJob/MeteredTriggerHelper.cs#L116-L117)).

| Scenario | Where triggered | Config gate (seeded default) | Template | Default subject |
|---|---|---|---|---|
| Usage event accepted by the Metering API (`StatusCode == "Accepted"`) | `UpdateSchedulerItem` → `SendSchedulerEmail` | `EnablesSuccessfulSchedulerEmail` (`False`) | `Accepted` | *Scheduled SaaS Metered Usage Submitted Successfully!* |
| Usage event rejected/errored (any other status code) | same | `EnablesFailureSchedulerEmail` (`False`) | `Failure` | *Scheduled SaaS Metered Usage Failure!* |
| A scheduled item's run time has **passed** and it never ran | `Execute` → `SendMissingEmail` | `EnablesMissingSchedulerEmail` (`False`) | `Missing` | *Scheduled SaaS Metered Task was Skipped!* |

Details:

- **Missing** mail is suppressed if `CheckIfSchedulerRun` shows the task already ran once
  ([MeteredTriggerHelper.cs:355-363](../src/MeteredTriggerJob/MeteredTriggerHelper.cs#L355-L363)) —
  it fires once per genuinely-skipped schedule, not on every job pass.
- Recipients come **only** from the `SchedulerEmailTo` application-config value (seeded empty).
  If it is empty, `PrepareMeteredEmailContent` **throws**
  ([EmailHelper.cs:130-133](../src/Services/Helpers/EmailHelper.cs#L130-L133)); the exception is
  caught and logged by the job, so a metered run is not lost — but no mail goes out.
  Plan-level `SuccessStateEmails` / `CopyToCustomer` do **not** apply to this family.
- Body placeholders substituted directly in C#, not via a stored proc
  ([EmailHelper.cs:134](../src/Services/Helpers/EmailHelper.cs#L134)):
  `****SubscriptionName****`, `****SchedulerTaskName****`, `****ResponseJson****`.

**Behavioural quirk worth knowing:** the gate check is an `OR` —
`if (enablesFailureSchedulerEmail || enablesSuccessfulSchedulerEmail)`
([MeteredTriggerHelper.cs:296](../src/MeteredTriggerJob/MeteredTriggerHelper.cs#L296)) — and
`SendSchedulerEmail` then picks the template purely from the status code. Turning on *either*
flag turns on *both* success and failure mail.

---

## 4. SMTP configuration

Every connection setting is read from the `ApplicationConfiguration` table per send (not from
`appsettings.json`), in `EmailHelper.FinalizeContentEmail`
([EmailHelper.cs:137-154](../src/Services/Helpers/EmailHelper.cs#L137-L154)):

| Config key | Purpose | Seeded default |
|---|---|---|
| `SMTPHost` | Server hostname | *(empty)* |
| `SMTPPort` | Port; falls back to `0` if unparseable | *(empty)* |
| `SMTPUserName` | Auth username | *(empty)* |
| `SMTPPassword` | Auth password | *(empty)* |
| `SMTPSslEnabled` | `EnableSsl`; falls back to `false` if unparseable | *(empty)* |
| `SMTPFromEmail` | `From` address | *(empty)* |
| `ApplicationName` | `${ApplicationName}` placeholder in bodies | `Contoso` |

Edited in the admin portal under **Application Config**. All mail is sent as HTML
(`IsBodyHtml = true`).

### Failure handling

`SMTPEmailService.SendEmail` never throws. `SmtpException` and general exceptions are both
caught and written to `ApplicationLog`
([SMTPEmailService.cs:89-98](../src/Services/Services/SMTPEmailService.cs#L89-L98)). A failed
send is therefore **silent** to the user and to the calling flow — the only evidence is a row in
`ApplicationLog` reading `"<subject>: SMTP exception <message>."`. Successful sends log
`"<subject>: Email sent succesfully!"` (sic).

There is no retry and no dead-letter for email.

### Known defect: CC

`emailContent.CCEmails` is populated by `EmailHelper` from the template's `Cc` column, but
`SMTPEmailService.SendEmail` never applies it to the `MailMessage` — **CC is silently dropped**.
Anything entered in the admin portal's CC field has no effect.

BCC previously had the same class of bug (the loop iterated the *To* list, so BCC addresses were
never used and To recipients were duplicated instead). That is fixed — `To` and `Bcc` are now each
split from their own field, blank entries are skipped, and addresses are trimmed
([SMTPEmailService.cs:70-90](../src/Services/Services/SMTPEmailService.cs#L70-L90)). A BCC-only
send (empty `To`) now works, which matches what the outer guard at
[SMTPEmailService.cs:50](../src/Services/Services/SMTPEmailService.cs#L50) has always allowed.

---

## 5. Quick reference — will an email be sent?

```
Customer clicks Activate (portal)         → yes, if IsEmailEnabledForSubscriptionActivation
Customer auto-activated on portal entry   → yes, same flag (best-effort, failures swallowed)
Admin clicks Activate (admin site)        → yes, same flag
Customer/admin clicks Deactivate          → yes, if IsEmailEnabledForUnsubscription
Subscription left in PendingActivation    → only if IsEmailEnabledForPendingActivation (default off)
Activation / unsubscribe failed           → yes, "Failed" template, via the same flags
Webhook: Unsubscribed                     → yes, if IsEmailEnabledForUnsubscription
Webhook: ChangePlan / ChangeQuantity /
         Reinstated / Renewed / Suspended  → NO email at all
Metered usage posted OK                   → yes, if Enables{Successful|Failure}SchedulerEmail
Metered usage failed                      → yes, if Enables{Successful|Failure}SchedulerEmail
Metered schedule missed its window        → yes, if EnablesMissingSchedulerEmail and never run before
Any of the above with empty To and Bcc    → no send; ApplicationLog row explains why
```

---

## 6. Files involved

| File | Role |
|---|---|
| [src/Services/Services/SMTPEmailService.cs](../src/Services/Services/SMTPEmailService.cs) | The only transport; `SmtpClient.Send` |
| [src/Services/Contracts/IEmailService.cs](../src/Services/Contracts/IEmailService.cs) | `SendEmail(EmailContentModel)` |
| [src/Services/Helpers/EmailHelper.cs](../src/Services/Helpers/EmailHelper.cs) | Builds subject/body/recipients/SMTP settings |
| [src/Services/Models/EmailContentModel.cs](../src/Services/Models/EmailContentModel.cs) | The DTO handed to the transport |
| [src/Services/StatusHandlers/NotificationStatusHandler.cs](../src/Services/StatusHandlers/NotificationStatusHandler.cs) | Lifecycle trigger + enable-flag gating |
| [src/Services/Services/MeteredPlanSchedulerManagementService.cs](../src/Services/Services/MeteredPlanSchedulerManagementService.cs) | `SendSchedulerEmail` |
| [src/MeteredTriggerJob/MeteredTriggerHelper.cs](../src/MeteredTriggerJob/MeteredTriggerHelper.cs) | Scheduler run loop; success/failure/missing decisions |
| [src/DataAccess/Services/EmailTemplateRepository.cs](../src/DataAccess/Services/EmailTemplateRepository.cs) | Template lookup + `spGetFormattedEmailBody` call |
| [src/DataAccess/Migrations/Custom/BaselineV2_Seed.cs](../src/DataAccess/Migrations/Custom/BaselineV2_Seed.cs) | Stored proc, SMTP config seeds, lifecycle templates |
| [src/DataAccess/Migrations/Custom/BaselineV7_Seed.cs](../src/DataAccess/Migrations/Custom/BaselineV7_Seed.cs) | Scheduler config seeds + `Accepted`/`Failure`/`Missing` templates |
| [src/AdminSite/Controllers/ApplicationConfigController.cs](../src/AdminSite/Controllers/ApplicationConfigController.cs) | Admin UI for editing templates and SMTP config |
| [src/AdminSite/Views/Plans/PlanDetails.cshtml](../src/AdminSite/Views/Plans/PlanDetails.cshtml) | Per-plan `SuccessStateEmails` / `CopyToCustomer` |

`IEmailService` is registered as scoped in all three hosts:
[AdminSite/Startup.cs:266](../src/AdminSite/Startup.cs#L266),
[CustomerSite/Startup.cs:299](../src/CustomerSite/Startup.cs#L299),
[MeteredTriggerJob/Program.cs:54](../src/MeteredTriggerJob/Program.cs#L54).
