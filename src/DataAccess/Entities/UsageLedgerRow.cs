using System;

namespace Marketplace.SaaS.Accelerator.DataAccess.Entities;

/// <summary>
/// Read-only projection of one dbo.UsageLedger row. NOT an EF entity: the UsageLedger
/// table's DDL is owned by the external RauMetering pipeline deployment, so this type
/// must never be added to SaasKitContext or appear in the model snapshot / migrations.
/// It is populated only by <see cref="Services.UsageLedgerReadRepository"/> via raw SQL.
/// </summary>
public class UsageLedgerRow
{
    public long Id { get; set; }

    public Guid AmpSubscriptionId { get; set; }

    public string DimensionId { get; set; }

    public string PlanId { get; set; }

    public Guid TenantId { get; set; }

    public DateTime PeriodStartUtc { get; set; }

    public DateTime PeriodEndUtc { get; set; }

    public DateTime EffectiveStartUtc { get; set; }

    public int? ActiveUserCount { get; set; }

    public int? ThresholdApplied { get; set; }

    public DateTime? WindowStartUtc { get; set; }

    public DateTime? WindowEndUtc { get; set; }

    public DateTime? SnapshotDateUtc { get; set; }

    public byte? SnapshotRunMode { get; set; }

    public DateTime? SnapshotComputedUtc { get; set; }

    public int Units { get; set; }

    public string Status { get; set; }

    public string SkipReason { get; set; }

    public int Attempts { get; set; }

    public DateTime? LastAttemptUtc { get; set; }

    public string LastResponse { get; set; }

    public string MsftUsageEventId { get; set; }

    public DateTime? AcceptedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// A pending row older than 18h (measured from EffectiveStartUtc, which is what
    /// Microsoft's 24h acceptance window is anchored to) is within ~6h of becoming
    /// permanently unbillable.
    /// </summary>
    public bool IsNearCliff(DateTime utcNow) =>
        string.Equals(this.Status, "pending", StringComparison.OrdinalIgnoreCase)
        && this.EffectiveStartUtc < utcNow.AddHours(-18);
}
