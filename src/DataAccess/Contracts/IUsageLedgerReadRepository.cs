using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.DataAccess.Contracts;

/// <summary>
/// Read-only access to dbo.UsageLedger, the billing ledger written by the external
/// RauMetering pipeline. Strictly read-only: the table's DDL and its rows are owned
/// by that pipeline; this repository must never insert, update, delete, or migrate.
/// Every member degrades to an empty/zeroed result when the table does not exist yet
/// (it is created by the pipeline writer's first run).
/// </summary>
public interface IUsageLedgerReadRepository
{
    /// <summary>Ledger rows for one subscription, newest period first (24 ≈ two years of monthly periods).</summary>
    Task<List<UsageLedgerRow>> GetForSubscriptionAsync(Guid ampSubscriptionId, int top = 24);

    /// <summary>Most recent ledger rows fleet-wide, optionally filtered to one status.</summary>
    Task<List<UsageLedgerRow>> GetRecentAsync(string statusFilter = null, int top = 200);

    /// <summary>Status counts for the health strip, including the pending-near-cliff count.</summary>
    Task<UsageLedgerHealth> GetHealthAsync();

    /// <summary>Whether dbo.UsageLedger exists yet.</summary>
    Task<bool> TableExistsAsync();
}
