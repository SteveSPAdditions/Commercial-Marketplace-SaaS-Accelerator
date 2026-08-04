namespace Marketplace.SaaS.Accelerator.DataAccess.Entities;

/// <summary>
/// Fleet-wide UsageLedger status counts for the AdminSite health strip. NOT an EF
/// entity (see <see cref="UsageLedgerRow"/>); an all-zeros instance is the correct
/// shape when the table does not exist yet.
/// </summary>
public class UsageLedgerHealth
{
    public int Pending { get; set; }

    /// <summary>Pending rows older than 18h — within ~6h of the 24h submission deadline.</summary>
    public int PendingNearCliff { get; set; }

    public int Accepted { get; set; }

    public int Duplicate { get; set; }

    public int Expired { get; set; }

    public int Zero { get; set; }

    public int Skipped { get; set; }
}
