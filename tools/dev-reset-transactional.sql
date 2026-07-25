/* =====================================================================
   DEV-ONLY transactional reset for a clean end-to-end onboarding test.

   Wipes per-subscription / per-tenant TRANSACTIONAL rows so a fresh
   Marketplace purchase can be walked through Setup from scratch.

   DELIBERATELY PRESERVES all seed / configuration tables -- Events,
   PlanEventsMapping, EmailTemplate, ApplicationConfiguration, Plans,
   Offers, etc. Emptying Events / PlanEventsMapping is what caused the
   landing-page NullReferenceException (activation-email path derefs a
   null subscriptionEvent); this script must NOT touch them.

   After running this, drop the RAU per-tenant database separately so
   InitialiseSaasTenant recreates it, and clear the E5 tenant artifacts
   (delete the enterprise app SP + the SaaS subscription to re-purchase).

   SAFETY: set @Confirm = 'YES-WIPE-DEV' below to actually run. Verify
   you are connected to the DEV AMP database (e.g. rauAMPSaaSDB) first.
   ===================================================================== */

DECLARE @Confirm varchar(20) = 'YES-WIPE-DEV';   -- <-- set to 'YES-WIPE-DEV' to execute

IF @Confirm <> 'YES-WIPE-DEV'
BEGIN
    RAISERROR('Refusing to run: set @Confirm = ''YES-WIPE-DEV'' to confirm this DEV wipe.', 16, 1);
    RETURN;
END

PRINT 'Transactional reset running against database: ' + DB_NAME();

SET XACT_ABORT ON;
BEGIN TRAN;

    -- Dependents on Subscriptions first (FK-safe). OBJECT_ID guards keep the
    -- script resilient to schema-version differences (a missing table is skipped).
    IF OBJECT_ID('dbo.SubscriptionAuditLogs','U')      IS NOT NULL DELETE FROM dbo.SubscriptionAuditLogs;
    IF OBJECT_ID('dbo.SubscriptionAttributeValues','U') IS NOT NULL DELETE FROM dbo.SubscriptionAttributeValues;
    IF OBJECT_ID('dbo.WebJobSubscriptionStatus','U')   IS NOT NULL DELETE FROM dbo.WebJobSubscriptionStatus;
    IF OBJECT_ID('dbo.MeteredAuditLogs','U')           IS NOT NULL DELETE FROM dbo.MeteredAuditLogs;
    IF OBJECT_ID('dbo.SubscriptionSite','U')           IS NOT NULL DELETE FROM dbo.SubscriptionSite;

    -- RAU signalling + consent/region/fan-out state (keyed by AmpSubscriptionId).
    IF OBJECT_ID('dbo.SubscriptionTenantConsent','U')  IS NOT NULL DELETE FROM dbo.SubscriptionTenantConsent;
    IF OBJECT_ID('dbo.NotificationOutbox','U')         IS NOT NULL DELETE FROM dbo.NotificationOutbox;
    IF OBJECT_ID('dbo.WebhookOperationLog','U')        IS NOT NULL DELETE FROM dbo.WebhookOperationLog;
    IF OBJECT_ID('dbo.WebhookCapture','U')             IS NOT NULL DELETE FROM dbo.WebhookCapture;

    -- The subscriptions themselves.
    IF OBJECT_ID('dbo.Subscriptions','U')              IS NOT NULL DELETE FROM dbo.Subscriptions;

    -- Log noise (optional -- keeps ApplicationLog clean for the next run).
    IF OBJECT_ID('dbo.ApplicationLog','U')             IS NOT NULL DELETE FROM dbo.ApplicationLog;

COMMIT;

/* PRESERVED (never deleted here): ApplicationConfiguration, EmailTemplate,
   Events, PlanEventsMapping, Plans, Offers, OfferAttributes,
   PlanAttributeMapping, Roles, ValueTypes, KnownUsers, Users,
   SchedulerFrequency, MeteredDimensions, MeteredPlanSchedulerManagement,
   DatabaseVersionHistory. */

-- Sanity check: the seed tables MUST still be populated, or the landing page
-- will NRE again on the next activation. Any 0 here = do NOT test until re-seeded.
SELECT 'Events'                   AS SeedTable, COUNT(*) AS [Rows] FROM dbo.Events
UNION ALL SELECT 'PlanEventsMapping',        COUNT(*) FROM dbo.PlanEventsMapping
UNION ALL SELECT 'EmailTemplate',            COUNT(*) FROM dbo.EmailTemplate
UNION ALL SELECT 'ApplicationConfiguration', COUNT(*) FROM dbo.ApplicationConfiguration
UNION ALL SELECT 'Plans',                    COUNT(*) FROM dbo.Plans
UNION ALL SELECT 'Offers',                   COUNT(*) FROM dbo.Offers;
