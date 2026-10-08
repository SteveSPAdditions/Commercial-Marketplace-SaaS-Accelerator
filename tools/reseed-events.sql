/* =====================================================================
   Restore the Events seed rows (Activate / Unsubscribe / Pending Activation).

   WHY: the Plan Details > Events tab is driven by spGetPlanEvents, which
   LEFT JOINs from dbo.Events. If Events is empty the tab shows nothing,
   CopyToCustomer / SuccessStateEmails can't be set, and EmailHelper falls
   back to the EmailTemplate To/Cc/Bcc only. Emptying Events is also what
   caused the July 2026 landing-page NRE (see commit 812d0df).

   Idempotent: inserts only the names that are missing. Does NOT touch
   PlanEventsMapping, EmailTemplate or ApplicationConfiguration.

   Run against the AMP database (dev: rauAMPSaaSDB on rau-sql).
   ===================================================================== */

SET NOCOUNT ON;

PRINT '--- Before ---';
SELECT 'Events'            AS SeedTable, COUNT(*) AS [Rows] FROM dbo.Events
UNION ALL SELECT 'PlanEventsMapping', COUNT(*) FROM dbo.PlanEventsMapping
UNION ALL SELECT 'EmailTemplate',     COUNT(*) FROM dbo.EmailTemplate;

SELECT EventsId, EventsName, IsActive FROM dbo.Events ORDER BY EventsId;

INSERT INTO dbo.Events (EventsName, IsActive, CreateDate)
SELECT v.EventsName, 1, GETDATE()
FROM (VALUES ('Activate'), ('Unsubscribe'), ('Pending Activation')) AS v(EventsName)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Events e WHERE e.EventsName = v.EventsName);

PRINT CONCAT('Inserted ', @@ROWCOUNT, ' missing Events row(s).');

-- Make sure the seeded names are active; the proc filters on IsActive = 1.
UPDATE dbo.Events SET IsActive = 1
WHERE EventsName IN ('Activate', 'Unsubscribe', 'Pending Activation')
  AND (IsActive IS NULL OR IsActive = 0);

PRINT '--- After ---';
SELECT EventsId, EventsName, IsActive FROM dbo.Events ORDER BY EventsId;

-- Mapping rows whose EventId no longer matches an Events row. These happen
-- when Events was emptied and re-identity'd. They are harmless (the proc
-- ignores them) but will never be read again; delete them if you want tidy.
PRINT '--- Orphaned PlanEventsMapping rows (EventId not in Events) ---';
SELECT m.Id, m.PlanId, m.EventId, m.CopyToCustomer, m.SuccessStateEmails
FROM dbo.PlanEventsMapping m
LEFT JOIN dbo.Events e ON e.EventsId = m.EventId
WHERE e.EventsId IS NULL;
