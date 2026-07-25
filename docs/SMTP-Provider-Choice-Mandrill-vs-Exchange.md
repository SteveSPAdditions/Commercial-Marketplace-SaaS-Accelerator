# SMTP Provider Choice — Mandrill vs Exchange Shared Mailbox

**Status:** advisory / decision notes. Written 2026-07-25.

**Question:** when configuring SMTP for the SaaS Accelerator, should we use our Mandrill service or an
Exchange shared mailbox in the SPAdditions tenant?

---

## Recommendation: Mandrill

The deciding factor is in the code, not the mail policy.

### 1. The accelerator can only do basic auth

`src/Services/Services/SMTPEmailService.cs:60-62` uses `System.Net.Mail.SmtpClient` with a
`NetworkCredential` — there is no XOAUTH2 path in it. Microsoft has been retiring Basic auth for SMTP
AUTH client submission in Exchange Online (the announced permanent-disable deadline was March 2026 —
confirm what the tenant actually does today).

If it is off, the shared-mailbox route is not a config change at all: it means swapping `SmtpClient`
for MailKit + an OAuth client-credentials flow + a `Mail.Send` app permission scoped with an
ApplicationAccessPolicy. That lands in `Services`, so it hits AdminSite, CustomerSite **and**
MeteredTriggerJob (`src/MeteredTriggerJob/Program.cs:54`).

Mandrill is six `ApplicationConfiguration` rows and no code.

### 2. Credential blast radius

The SMTP password lives in the `ApplicationConfiguration` table in SQL as plaintext
(`src/Services/Helpers/EmailHelper.cs:148`) and is editable from the AdminSite UI — masked on display
only. Note Key Vault references do **not** help here: these values come from the DB, not appsettings.

A Mandrill API key is send-only and revocable in one click. A tenant mailbox credential sitting in a
DB row is a different class of exposure.

### 3. Failures are near-invisible today

`src/Services/Services/SMTPEmailService.cs:89-98` swallows `SmtpException` into an ApplicationLog
line. Mandrill gives delivery logs, bounces and webhooks *outside* the app — exactly what is needed
for activation emails and for the trial-abuse alert in
`docs/Free-Trial-Reuse-Guard-Options.md`, where "did it actually send?" is the whole point.

### 4. Throttling

Exchange Online client submission is roughly 30 messages/minute and 10k recipients/day per mailbox.
Fine at current volume, but it fails hard at a spike (e.g. a replay run through the Postman lifecycle
collection) rather than queueing.

### 5. Reputation

Transactional mail from a human shared mailbox mixes automated bounces and spam complaints into the
corporate domain's sending reputation.

---

## Where the shared mailbox genuinely wins

**Replies.** Customers *will* reply to an activation email, and Mandrill will not receive them.

The fix is a Reply-To header pointing at the shared mailbox — but note `EmailContentModel` has no
`ReplyTo` property, so that is a small addition (one field + one line in `SMTPEmailService` + a config
key). Worth doing regardless of which provider is chosen.

---

## Config mapping (Mandrill)

| ApplicationConfiguration key | Value |
| --- | --- |
| `SMTPHost` | `smtp.mandrillapp.com` |
| `SMTPPort` | `587` |
| `SMTPSslEnabled` | `true` (means STARTTLS on 587 in .NET) |
| `SMTPUserName` | Mandrill account username |
| `SMTPPassword` | a **dedicated** Mandrill API key for the accelerator, so it can be revoked without affecting other senders |
| `SMTPFromEmail` | a verified `@spadditions.com` sender |

Seeded (empty) by `src/DataAccess/Migrations/Custom/BaselineV2_Seed.cs:371-376`; documented in
`deployment/README.md:53-58`.

**Before switching:** add Mandrill's DKIM records for the sending domain and check the DMARC policy —
if spadditions.com is at `p=quarantine` / `p=reject`, unaligned mail will be dropped silently.

---

## Bug to fix while in there

`src/Services/Services/SMTPEmailService.cs:76-81` adds `toEmails` to the BCC list instead of
`BCCEmails` — so any configured BCC sends a duplicate to the To addresses and never reaches the
intended BCC recipient. Relevant if an alerting address is ever configured via BCC.

---

## Related

- `docs/Free-Trial-Reuse-Guard-Options.md` — the trial-abuse alert email that depends on this
