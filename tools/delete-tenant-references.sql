/* =====================================================================
   Remove every AMP-DB reference to ONE purchaser tenant.

   Target tenant : 71150C86-EF73-422D-8FDA-43879C95E0B4
   Scope         : the AMP (SaaS Accelerator) database only, e.g. rauAMPSaaSDB.

   What this does
   --------------
   1. Resolves the set of AMP subscription ids that belong to the tenant.
      A subscription is "the tenant's" if ANY of these hold:
        - Subscriptions.PurchaserTenantId        = @TenantId
        - SubscriptionTenantConsent.TenantId     = @TenantId
        - UsageLedger.TenantId                   = @TenantId
        - WebhookCapture.PayloadJson  contains the tenant GUID text
        - NotificationOutbox.EventJson contains the tenant GUID text
        - any id you add by hand in the @ExtraSubscriptionIds block below
          (use this for synthetic / simulator subscription ids that were
          inserted without a PurchaserTenantId)
   2. In DRY-RUN mode (the default) it only PRINTS / SELECTs what it
      would delete, per table, and returns. Nothing is changed.
   3. With @DryRun = 0 AND @Confirm = 'YES-DELETE-TENANT' it deletes, in
      FK-safe order, inside one transaction, and prints row counts.

   Tables touched (children first)
   -------------------------------
     SubscriptionAuditLogs           (int  SubscriptionId -> Subscriptions.Id)
     MeteredAuditLogs                (int  SubscriptionId -> Subscriptions.Id)
     MeteredPlanSchedulerManagement  (int  SubscriptionId -> Subscriptions.Id)
     SubscriptionAttributeValues     (guid SubscriptionId = AMPSubscriptionId)
     WebJobSubscriptionStatus        (guid SubscriptionId = AMPSubscriptionId)
     SubscriptionSite                (guid AmpSubscriptionId)
     SubscriptionTenantConsent       (guid AmpSubscriptionId  OR TenantId)
     NotificationOutbox              (guid AmpSubscriptionId  OR EventJson text)
     WebhookOperationLog             (guid SubscriptionId)
     WebhookCapture                  (guid SubscriptionId     OR PayloadJson text)
     UsageLedger                     (guid AMPSubscriptionId  OR TenantId)
     Subscriptions                   (guid AMPSubscriptionId)
     ApplicationLog                  (LogDetail text mentions tenant or a sub id)
                                     -- controlled by @IncludeApplicationLog

   Deliberately NOT touched
   ------------------------
     Users / KnownUsers      keyed by e-mail, may be shared with other tenants;
                             a report of the purchaser's Users row(s) is
                             printed so you can decide separately.
     Seed / config tables    Plans, Offers, Events, PlanEventsMapping,
                             EmailTemplate, ApplicationConfiguration, etc.x
     Anything outside AMP    the regional MasterDb{region}.TenantRegions rows,
                             the RAU per-tenant database, and the real
                             Marketplace subscription on Microsoft's side.

   SAFETY: run once with @DryRun = 1, read the output, then flip both
   @DryRun and @Confirm. Check DB_NAME() in the first PRINT line.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @TenantId              uniqueidentifier = '71150C86-EF73-422D-8FDA-43879C95E0B4';
DECLARE @DryRun                bit              = 0;     -- 1 = report only, 0 = delete
DECLARE @Confirm               varchar(30)      = 'xYES-DELETE-TENANT';    -- set to 'YES-DELETE-TENANT' to delete
DECLARE @IncludeApplicationLog bit              = 1;     -- 0 = leave ApplicationLog alone

DECLARE @TenantText nvarchar(36) = CONVERT(nvarchar(36), @TenantId);

PRINT 'Tenant purge (' + CASE WHEN @DryRun = 1 THEN 'DRY RUN' ELSE 'LIVE' END
    + ') for ' + @TenantText + ' against database: ' + DB_NAME();

/* ---------------------------------------------------------------------
   1. Resolve the tenant's subscription ids
   --------------------------------------------------------------------- */
IF OBJECT_ID('tempdb..#Subs')    IS NOT NULL DROP TABLE #Subs;
IF OBJECT_ID('tempdb..#SubInts') IS NOT NULL DROP TABLE #SubInts;
IF OBJECT_ID('tempdb..#Preview') IS NOT NULL DROP TABLE #Preview;

CREATE TABLE #Subs    (AmpSubscriptionId uniqueidentifier NOT NULL PRIMARY KEY, Source nvarchar(80) NOT NULL);
CREATE TABLE #SubInts (Id int NOT NULL PRIMARY KEY);
CREATE TABLE #Preview (Seq int IDENTITY(1,1), [Table] sysname, MatchedRows int);

-- @ExtraSubscriptionIds: add synthetic / simulator subscription ids here
-- that are known to belong to this tenant but carry no PurchaserTenantId.
-- INSERT INTO #Subs (AmpSubscriptionId, Source) VALUES
--     ('00000000-0000-0000-0000-000000000000', 'manual');

INSERT INTO #Subs (AmpSubscriptionId, Source)
SELECT s.AMPSubscriptionId, 'Subscriptions.PurchaserTenantId'
FROM dbo.Subscriptions s
WHERE s.PurchaserTenantId = @TenantId
  AND NOT EXISTS (SELECT 1 FROM #Subs x WHERE x.AmpSubscriptionId = s.AMPSubscriptionId);

IF OBJECT_ID('dbo.SubscriptionTenantConsent','U') IS NOT NULL
INSERT INTO #Subs (AmpSubscriptionId, Source)
SELECT DISTINCT c.AmpSubscriptionId, 'SubscriptionTenantConsent.TenantId'
FROM dbo.SubscriptionTenantConsent c
WHERE c.TenantId = @TenantId
  AND NOT EXISTS (SELECT 1 FROM #Subs x WHERE x.AmpSubscriptionId = c.AmpSubscriptionId);

IF OBJECT_ID('dbo.UsageLedger','U') IS NOT NULL
INSERT INTO #Subs (AmpSubscriptionId, Source)
SELECT DISTINCT l.AMPSubscriptionId, 'UsageLedger.TenantId'
FROM dbo.UsageLedger l
WHERE l.TenantId = @TenantId
  AND NOT EXISTS (SELECT 1 FROM #Subs x WHERE x.AmpSubscriptionId = l.AMPSubscriptionId);

IF OBJECT_ID('dbo.WebhookCapture','U') IS NOT NULL
INSERT INTO #Subs (AmpSubscriptionId, Source)
SELECT DISTINCT w.SubscriptionId, 'WebhookCapture.PayloadJson'
FROM dbo.WebhookCapture w
WHERE w.SubscriptionId IS NOT NULL
  AND w.PayloadJson COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%'
  AND NOT EXISTS (SELECT 1 FROM #Subs x WHERE x.AmpSubscriptionId = w.SubscriptionId);

IF OBJECT_ID('dbo.NotificationOutbox','U') IS NOT NULL
INSERT INTO #Subs (AmpSubscriptionId, Source)
SELECT DISTINCT o.AmpSubscriptionId, 'NotificationOutbox.EventJson'
FROM dbo.NotificationOutbox o
WHERE o.EventJson COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%'
  AND NOT EXISTS (SELECT 1 FROM #Subs x WHERE x.AmpSubscriptionId = o.AmpSubscriptionId);

-- Internal int keys for the audit / scheduler tables.
INSERT INTO #SubInts (Id)
SELECT s.Id
FROM dbo.Subscriptions s
JOIN #Subs x ON x.AmpSubscriptionId = s.AMPSubscriptionId;

/* ---------------------------------------------------------------------
   2. Report what was found (always shown, dry run or live)
   --------------------------------------------------------------------- */
SELECT 'Resolved subscription ids' AS Section, x.AmpSubscriptionId, x.Source
FROM #Subs x
ORDER BY x.Source, x.AmpSubscriptionId;

SELECT 'Subscriptions rows' AS Section,
       s.Id, s.AMPSubscriptionId, s.Name, s.SubscriptionStatus, s.AMPPlanId, s.AmpOfferId,
       s.PurchaserEmail, s.PurchaserTenantId, s.IsFreeTrial, s.CreateDate, s.ModifyDate
FROM dbo.Subscriptions s
JOIN #Subs x ON x.AmpSubscriptionId = s.AMPSubscriptionId
ORDER BY s.CreateDate;

-- Per-table match counts.
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'SubscriptionAuditLogs', COUNT(*) FROM dbo.SubscriptionAuditLogs a
WHERE a.SubscriptionId IN (SELECT Id FROM #SubInts);

INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'MeteredAuditLogs', COUNT(*) FROM dbo.MeteredAuditLogs m
WHERE m.SubscriptionId IN (SELECT Id FROM #SubInts);

IF OBJECT_ID('dbo.MeteredPlanSchedulerManagement','U') IS NOT NULL
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'MeteredPlanSchedulerManagement', COUNT(*) FROM dbo.MeteredPlanSchedulerManagement p
WHERE p.SubscriptionId IN (SELECT Id FROM #SubInts);

INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'SubscriptionAttributeValues', COUNT(*) FROM dbo.SubscriptionAttributeValues v
WHERE v.SubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);

INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'WebJobSubscriptionStatus', COUNT(*) FROM dbo.WebJobSubscriptionStatus j
WHERE j.SubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);

IF OBJECT_ID('dbo.SubscriptionSite','U') IS NOT NULL
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'SubscriptionSite', COUNT(*) FROM dbo.SubscriptionSite ss
WHERE ss.AmpSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);

IF OBJECT_ID('dbo.SubscriptionTenantConsent','U') IS NOT NULL
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'SubscriptionTenantConsent', COUNT(*) FROM dbo.SubscriptionTenantConsent c
WHERE c.TenantId = @TenantId
   OR c.AmpSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);

IF OBJECT_ID('dbo.NotificationOutbox','U') IS NOT NULL
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'NotificationOutbox', COUNT(*) FROM dbo.NotificationOutbox o
WHERE o.AmpSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs)
   OR o.EventJson COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%';

IF OBJECT_ID('dbo.WebhookOperationLog','U') IS NOT NULL
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'WebhookOperationLog', COUNT(*) FROM dbo.WebhookOperationLog w
WHERE w.SubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);

IF OBJECT_ID('dbo.WebhookCapture','U') IS NOT NULL
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'WebhookCapture', COUNT(*) FROM dbo.WebhookCapture w
WHERE w.SubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs)
   OR w.PayloadJson COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%';

IF OBJECT_ID('dbo.UsageLedger','U') IS NOT NULL
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'UsageLedger', COUNT(*) FROM dbo.UsageLedger l
WHERE l.TenantId = @TenantId
   OR l.AMPSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);

INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'Subscriptions', COUNT(*) FROM dbo.Subscriptions s
WHERE s.AMPSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);

IF @IncludeApplicationLog = 1
INSERT INTO #Preview ([Table], MatchedRows)
SELECT 'ApplicationLog', COUNT(*) FROM dbo.ApplicationLog a
WHERE a.LogDetail COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%'
   OR EXISTS (SELECT 1 FROM #Subs x
              WHERE a.LogDetail COLLATE Latin1_General_CI_AS
                    LIKE '%' + CONVERT(nvarchar(36), x.AmpSubscriptionId) + '%');

SELECT 'Rows that would be deleted' AS Section, p.[Table], p.MatchedRows
FROM #Preview p
ORDER BY p.Seq;

-- Users rows for the purchaser e-mail(s). NOT deleted by this script:
-- OtherSubscriptions > 0 means the same login owns subscriptions for other
-- tenants and the Users row must stay.
SELECT 'Users (not deleted)' AS Section,
       u.UserId, u.EmailAddress, u.FullName, u.CreatedDate,
       (SELECT COUNT(*) FROM dbo.Subscriptions s2
         WHERE s2.UserId = u.UserId
           AND s2.AMPSubscriptionId NOT IN (SELECT AmpSubscriptionId FROM #Subs)) AS OtherSubscriptions
FROM dbo.Users u
WHERE u.UserId IN (SELECT s.UserId FROM dbo.Subscriptions s
                   JOIN #Subs x ON x.AmpSubscriptionId = s.AMPSubscriptionId)
   OR u.EmailAddress IN (SELECT s.PurchaserEmail FROM dbo.Subscriptions s
                         JOIN #Subs x ON x.AmpSubscriptionId = s.AMPSubscriptionId);

/* ---------------------------------------------------------------------
   3. Gate
   --------------------------------------------------------------------- */
IF @DryRun = 1
BEGIN
    PRINT 'DRY RUN complete. No rows deleted. Set @DryRun = 0 and @Confirm = ''YES-DELETE-TENANT'' to execute.';
    RETURN;
END

IF @Confirm <> 'YES-DELETE-TENANT'
BEGIN
    RAISERROR('Refusing to run: @DryRun = 0 but @Confirm is not ''YES-DELETE-TENANT''.', 16, 1);
    RETURN;
END

IF (SELECT ISNULL(SUM(MatchedRows), 0) FROM #Preview) = 0
BEGIN
    PRINT 'Nothing resolved for this tenant. Exiting without changes.';
    RETURN;
END

/* ---------------------------------------------------------------------
   4. Delete, children first, one transaction
   --------------------------------------------------------------------- */
BEGIN TRAN;

    DELETE a FROM dbo.SubscriptionAuditLogs a
    WHERE a.SubscriptionId IN (SELECT Id FROM #SubInts);
    PRINT 'SubscriptionAuditLogs          : ' + CAST(@@ROWCOUNT AS varchar(10));

    DELETE m FROM dbo.MeteredAuditLogs m
    WHERE m.SubscriptionId IN (SELECT Id FROM #SubInts);
    PRINT 'MeteredAuditLogs               : ' + CAST(@@ROWCOUNT AS varchar(10));

    IF OBJECT_ID('dbo.MeteredPlanSchedulerManagement','U') IS NOT NULL
    BEGIN
        DELETE p FROM dbo.MeteredPlanSchedulerManagement p
        WHERE p.SubscriptionId IN (SELECT Id FROM #SubInts);
        PRINT 'MeteredPlanSchedulerManagement : ' + CAST(@@ROWCOUNT AS varchar(10));
    END

    DELETE v FROM dbo.SubscriptionAttributeValues v
    WHERE v.SubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);
    PRINT 'SubscriptionAttributeValues    : ' + CAST(@@ROWCOUNT AS varchar(10));

    DELETE j FROM dbo.WebJobSubscriptionStatus j
    WHERE j.SubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);
    PRINT 'WebJobSubscriptionStatus       : ' + CAST(@@ROWCOUNT AS varchar(10));

    IF OBJECT_ID('dbo.SubscriptionSite','U') IS NOT NULL
    BEGIN
        DELETE ss FROM dbo.SubscriptionSite ss
        WHERE ss.AmpSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);
        PRINT 'SubscriptionSite               : ' + CAST(@@ROWCOUNT AS varchar(10));
    END

    IF OBJECT_ID('dbo.SubscriptionTenantConsent','U') IS NOT NULL
    BEGIN
        DELETE c FROM dbo.SubscriptionTenantConsent c
        WHERE c.TenantId = @TenantId
           OR c.AmpSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);
        PRINT 'SubscriptionTenantConsent      : ' + CAST(@@ROWCOUNT AS varchar(10));
    END

    IF OBJECT_ID('dbo.NotificationOutbox','U') IS NOT NULL
    BEGIN
        DELETE o FROM dbo.NotificationOutbox o
        WHERE o.AmpSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs)
           OR o.EventJson COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%';
        PRINT 'NotificationOutbox             : ' + CAST(@@ROWCOUNT AS varchar(10));
    END

    IF OBJECT_ID('dbo.WebhookOperationLog','U') IS NOT NULL
    BEGIN
        DELETE w FROM dbo.WebhookOperationLog w
        WHERE w.SubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);
        PRINT 'WebhookOperationLog            : ' + CAST(@@ROWCOUNT AS varchar(10));
    END

    IF OBJECT_ID('dbo.WebhookCapture','U') IS NOT NULL
    BEGIN
        DELETE w FROM dbo.WebhookCapture w
        WHERE w.SubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs)
           OR w.PayloadJson COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%';
        PRINT 'WebhookCapture                 : ' + CAST(@@ROWCOUNT AS varchar(10));
    END

    IF OBJECT_ID('dbo.UsageLedger','U') IS NOT NULL
    BEGIN
        DELETE l FROM dbo.UsageLedger l
        WHERE l.TenantId = @TenantId
           OR l.AMPSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);
        PRINT 'UsageLedger                    : ' + CAST(@@ROWCOUNT AS varchar(10));
    END

    DELETE s FROM dbo.Subscriptions s
    WHERE s.AMPSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs);
    PRINT 'Subscriptions                  : ' + CAST(@@ROWCOUNT AS varchar(10));

    IF @IncludeApplicationLog = 1
    BEGIN
        DELETE a FROM dbo.ApplicationLog a
        WHERE a.LogDetail COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%'
           OR EXISTS (SELECT 1 FROM #Subs x
                      WHERE a.LogDetail COLLATE Latin1_General_CI_AS
                            LIKE '%' + CONVERT(nvarchar(36), x.AmpSubscriptionId) + '%');
        PRINT 'ApplicationLog                 : ' + CAST(@@ROWCOUNT AS varchar(10));
    END

COMMIT;

/* ---------------------------------------------------------------------
   5. Post-check: every count here should be 0
   --------------------------------------------------------------------- */
SELECT 'Post-check' AS Section, 'Subscriptions.PurchaserTenantId' AS Ref, COUNT(*) AS Remaining
FROM dbo.Subscriptions WHERE PurchaserTenantId = @TenantId
UNION ALL SELECT 'Post-check', 'Subscriptions by resolved id', COUNT(*)
FROM dbo.Subscriptions WHERE AMPSubscriptionId IN (SELECT AmpSubscriptionId FROM #Subs)
UNION ALL SELECT 'Post-check', 'SubscriptionTenantConsent.TenantId', COUNT(*)
FROM dbo.SubscriptionTenantConsent WHERE TenantId = @TenantId
UNION ALL SELECT 'Post-check', 'UsageLedger.TenantId', COUNT(*)
FROM dbo.UsageLedger WHERE TenantId = @TenantId
UNION ALL SELECT 'Post-check', 'WebhookCapture.PayloadJson text', COUNT(*)
FROM dbo.WebhookCapture WHERE PayloadJson COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%'
UNION ALL SELECT 'Post-check', 'NotificationOutbox.EventJson text', COUNT(*)
FROM dbo.NotificationOutbox WHERE EventJson COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%'
UNION ALL SELECT 'Post-check', 'ApplicationLog.LogDetail text', COUNT(*)
FROM dbo.ApplicationLog WHERE LogDetail COLLATE Latin1_General_CI_AS LIKE '%' + @TenantText + '%';

DROP TABLE #Subs;
DROP TABLE #SubInts;
DROP TABLE #Preview;
