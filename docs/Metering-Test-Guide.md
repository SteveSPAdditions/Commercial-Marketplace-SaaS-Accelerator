# Metered Billing Test Guide

How to re-test the metered usage submission to Microsoft after subscribing (or
resubscribing) a tenant. Written 2026-09-20 against the RauMetering pipeline
(`rau-metering-writer` + `rau-metering-submit`) and the Accelerator fork at
that date.

> **There is no Microsoft sandbox for metering.** Every usage event Microsoft
> accepts is billed to the purchasing tenant. Test with the smallest quantity
> that proves the path, and clean up any hand-inserted rows afterwards.

## 1. How the pipeline decides when to submit

| Step | Who | When | What |
|---|---|---|---|
| Replicate term start | Accelerator `Activated` signal | On activation, ChangePlan, Renew | Copies `AMPSubscriptionId` and `MarketplaceTermStartUtc` (= `Subscriptions.StartDate`) into the regional `TenantRegions` row |
| Daily snapshot | `rau-metering-writer` `DailySnapshot` | 03:00 UTC daily | Counts notified users per tenant into the tenant DB. Never bills. |
| Boundary run | `rau-metering-writer` `BoundaryProcessing` | Every 15 min | If now is inside the **50 minutes before the monthly anniversary** of `MarketplaceTermStartUtc`, snapshots, nets `max(0, count - N)`, and inserts **one pending `UsageLedger` row** per subscription per period. Retries a missed window for up to 18 h. |
| Submit | `rau-metering-submit` `SubmitPending` | **:05 every hour** | Sends every `Status = 'pending'` row whose `EffectiveStartUtc` is under 23.5 h old to `batchUsageEvent`. Writes the response back to the row. Older rows are marked `expired` and never sent. |

So for a fresh subscription the first real submission is about one month after
activation, within the hour after the anniversary. Nothing is sent before that.

Ledger status flow: `pending -> accepted | duplicate | expired` (submitter),
plus `zero` and `skipped` written directly by the writer and never submitted.

## 2. Prerequisites (or the writer skips silently)

Check each of these before testing. A miss logs a skip reason and produces no
usage event.

1. **Paid plan.** `free-trial` is in `FreeTrialPlanIds` and is suppressed
   entirely (`SuppressedFreeTrial`). Subscribe on, or ChangePlan to,
   `standard-monthly` or `standard-annual`.
2. **TenantRegions row populated** in the regional MDB. The writer only sees
   rows where all three hold:
   ```sql
   SELECT TenantId, SubscriptionProvider, SubscriptionId, MarketplaceTermStartUtc, MeteredUserThreshold
   FROM dbo.TenantRegions
   WHERE TenantId = '<tenant id>';
   -- need: SubscriptionProvider = 'MarketplaceSaaS', SubscriptionId NOT NULL, MarketplaceTermStartUtc NOT NULL
   ```
3. **Plan id in the tenant DB.** The snapshot carries `MarketplacePlanId` from
   the tenant DB's `Subscriptions` table. Null logs `SkippedPlanUnknown`.
4. **A non-zero count.** At least one acknowledgement notification must have
   been sent in the tenant within the trailing window, or the writer inserts a
   terminal `zero` row and nothing is submitted.
5. **Subscription is `Subscribed`** on Microsoft's side. Usage for
   `PendingFulfillmentStart` or `Unsubscribed` is rejected.
6. Public plans need no `MeteredUserThreshold`. Only private-offer plans do.

Configured ids (authority is `RauMeteringWriter/local.settings.json`):

| Setting | Value |
|---|---|
| `MeteringDimensionId` | `notified_users` |
| `PublicPlanIds` | `free-trial,standard-monthly,standard-annual` |
| `FreeTrialPlanIds` | `free-trial` |

## 3. Ways to test sooner

Ordered from most to least faithful to production.

### 3.1 Backdate the term start (full pipeline)

Move the anniversary into the next boundary window. In the **regional MDB**
for the tenant's region:

```sql
-- Anniversary lands ~20 minutes from now; the next 15-minute boundary firing
-- is inside the 50-minute lead window.
UPDATE dbo.TenantRegions
SET MarketplaceTermStartUtc = DATEADD(MINUTE, 20, DATEADD(MONTH, -1, SYSUTCDATETIME()))
WHERE TenantId = '<tenant id>';
```

Then either wait for the boundary timer or fire it by hand (3.3). The writer
inserts the pending row; the submitter sends it at the next :05 (or on demand).

**Afterwards restore the real value**, or the next anniversary is wrong:

```sql
UPDATE dbo.TenantRegions
SET MarketplaceTermStartUtc = '<original value>'
WHERE TenantId = '<tenant id>';
```

Note the ledger's unique key is (`AMPSubscriptionId`, `DimensionId`,
`PeriodStartUtc`). The backdated period start is a synthetic date, so it will
not collide with the real period later. Leave the row in place as a record, or
delete it once verified.

### 3.2 Hand-insert a pending ledger row (submitter + Microsoft only)

Skips the writer entirely. Proves token acquisition, the batch call, and
Microsoft's acceptance. Run against the **AMP DB** (`rauAMPSaaSDB`):

```sql
DECLARE @Sub    uniqueidentifier = '<AMPSubscriptionId>';
DECLARE @Tenant uniqueidentifier = '<tenant id>';
DECLARE @Now    datetime2(0)     = SYSUTCDATETIME();

INSERT INTO dbo.UsageLedger
    (AMPSubscriptionId, DimensionId, PlanId, TenantId,
     PeriodStartUtc, PeriodEndUtc, EffectiveStartUtc,
     ActiveUserCount, ThresholdApplied, Units, Status, SkipReason)
VALUES
    (@Sub, 'notified_users', 'standard-monthly', @Tenant,
     DATEADD(MONTH, -1, @Now), @Now, @Now,
     1, 0, 1, 'pending', 'MANUAL TEST ROW');
```

`Attempts` and `CreatedUtc` have defaults. The submitter picks the row up at
the next :05 or when fired by hand (3.3).

**Delete the row after the test.** It occupies the unique key for its
`PeriodStartUtc`, and its `MsftUsageEventId` is a real billed event:

```sql
DELETE FROM dbo.UsageLedger WHERE SkipReason = 'MANUAL TEST ROW';
```

### 3.3 Fire the timers on demand

Both apps are timer-only, but the Functions host admin endpoint runs a timer
function immediately. Master key is under the Function App's
**App keys** blade in the portal.

```http
POST https://rau-metering-writer.azurewebsites.net/admin/functions/BoundaryProcessing
x-functions-key: <master key>
Content-Type: application/json

{}
```

```http
POST https://rau-metering-submit.azurewebsites.net/admin/functions/SubmitPending
x-functions-key: <master key>
Content-Type: application/json

{}
```

The portal's **Code + Test > Test/Run** on the function does the same thing.
A 202 means the host queued the run; check the function's Monitor blade or
Application Insights for the result.

Combine with 3.1 or 3.2 to get a result in minutes instead of waiting for the
15-minute and hourly timers.

### 3.4 AdminSite RecordUsage page (emergency only)

Posts a usage event directly, bypassing the ledger. A later boundary run for
the same hour can double bill because the ledger's unique key never saw the
manual event. Use it only to poke the API, not to test the pipeline.

## 4. Verifying the result

1. **UsageLedger row** (AMP DB, or the AdminSite ledger page):
   ```sql
   SELECT Id, AMPSubscriptionId, PlanId, PeriodStartUtc, EffectiveStartUtc, Units,
          Status, Attempts, LastAttemptUtc, MsftUsageEventId, AcceptedUtc, LastResponse
   FROM dbo.UsageLedger
   WHERE TenantId = '<tenant id>'
   ORDER BY Id DESC;
   ```
   `accepted` with a `MsftUsageEventId` is success. `duplicate` is also success
   (Microsoft already had an event for that subscription, dimension and hour).
   `LastResponse` holds the raw Microsoft JSON on rejection.
2. **Application Insights** events from the two apps:
   `MeteringBoundaryProcessed`, `MeteringSubmitHeartbeat`,
   `MeteringRowRejected`, `MeteringRowExpired`, `MeteringMissedWindow`,
   `MeteringPendingNearCliff`.
3. **Partner Center** usage reports show accepted events with a lag of a day or
   more. Trust the ledger row first.

## 5. Microsoft-side rules

- Only `Subscribed` subscriptions accept usage.
- `effectiveStartTime` must be within the previous 24 hours.
- One accepted event per subscription, dimension and hour. A second returns
  `Duplicate`.
- The dimension must belong to the subscription's current plan.
- Subscription GUIDs must be lowercase in the API call. The submitter handles
  this; a hand-built request must too.

## 6. Interaction with the tenant purge script

If `tools/delete-tenant-references.sql` was used to wipe a tenant from the AMP
DB, that tenant's `UsageLedger` rows and free-trial history are gone. A
resubscribe on `free-trial` auto-activates again but produces **no** metering
until the plan changes to a paid one, so test metering on `standard-monthly`.
