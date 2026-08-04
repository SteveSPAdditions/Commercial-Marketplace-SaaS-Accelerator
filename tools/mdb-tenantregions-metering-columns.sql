/* =====================================================================
   Regional MDB migration: metering columns on TenantRegions.

   Adds the two operational metering columns the metered-billing pipeline
   reads from every regional master DB (see active-user-snapshot-spec.md
   sections 2.2.3 and 3.2.1):

     MeteredUserThreshold     int       NULL  -- N, the per-month exempt
                                              -- user count for private-offer
                                              -- plans; NULL/0 for public plans
     MarketplaceTermStartUtc  datetime  NULL  -- current term start; monthly
                                              -- billing anniversaries derive
                                              -- from this

   The AMP subscription GUID needs no new column -- TenantRegions already
   carries it as SubscriptionId (populated by SaasAcceleratorEventHandler
   on TenantRegionFanOut).

   Run against EVERY regional master DB (the MasterDb{region} set). The
   TenantRegions table is OrmLite-managed (CreateTableIfNotExists only,
   which never alters existing tables), so existing databases need this
   script; a freshly created table picks the columns up from the updated
   TenantRegion model class when that ships.

   Idempotent: safe to re-run. datetime (not datetime2) to match the
   existing Created/Modified columns OrmLite emitted on this table.
   ===================================================================== */

PRINT 'TenantRegions metering columns -- running against database: ' + DB_NAME();

IF COL_LENGTH('dbo.TenantRegions', 'MeteredUserThreshold') IS NULL
BEGIN
    ALTER TABLE dbo.TenantRegions ADD MeteredUserThreshold int NULL;
    PRINT 'Added TenantRegions.MeteredUserThreshold';
END
ELSE
    PRINT 'TenantRegions.MeteredUserThreshold already present -- skipped';

IF COL_LENGTH('dbo.TenantRegions', 'MarketplaceTermStartUtc') IS NULL
BEGIN
    ALTER TABLE dbo.TenantRegions ADD MarketplaceTermStartUtc datetime NULL;
    PRINT 'Added TenantRegions.MarketplaceTermStartUtc';
END
ELSE
    PRINT 'TenantRegions.MarketplaceTermStartUtc already present -- skipped';
