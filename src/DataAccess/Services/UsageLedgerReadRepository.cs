using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Marketplace.SaaS.Accelerator.DataAccess.Services;

/// <summary>
/// Raw-SQL read-only repository over dbo.UsageLedger, using the EF connection but
/// deliberately bypassing the EF model: the table's DDL is owned by the external
/// RauMetering pipeline, so it must never become an entity, enter the model snapshot,
/// or be touched by migrations. Deliberately no INSERT/UPDATE/DELETE anywhere in this
/// type. Every query is guarded so a missing table (the pipeline writer has not run
/// yet) yields an empty/zeroed result instead of an exception.
/// </summary>
public class UsageLedgerReadRepository : IUsageLedgerReadRepository
{
    private readonly SaasKitContext context;

    private static readonly string[] ValidStatuses = { "pending", "accepted", "duplicate", "expired", "zero", "skipped" };

    // Column order must match ReadRow's ordinal reads.
    private const string SelectColumns = @"Id, AMPSubscriptionId, DimensionId, PlanId, TenantId,
       PeriodStartUtc, PeriodEndUtc, EffectiveStartUtc,
       ActiveUserCount, ThresholdApplied, WindowStartUtc, WindowEndUtc,
       SnapshotDateUtc, SnapshotRunMode, SnapshotComputedUtc,
       Units, Status, SkipReason, Attempts, LastAttemptUtc, LastResponse,
       MsftUsageEventId, AcceptedUtc, CreatedUtc";

    private const string TableGuard = "IF OBJECT_ID('dbo.UsageLedger','U') IS NULL RETURN;";

    public UsageLedgerReadRepository(SaasKitContext context)
    {
        this.context = context;
    }

    public async Task<List<UsageLedgerRow>> GetForSubscriptionAsync(Guid ampSubscriptionId, int top = 24)
    {
        var sql = $@"{TableGuard}
SELECT TOP (@top) {SelectColumns}
FROM dbo.UsageLedger
WHERE AMPSubscriptionId = @id
ORDER BY PeriodStartUtc DESC;";

        return await this.QueryRowsAsync(sql, cmd =>
        {
            AddParameter(cmd, "@top", top);
            AddParameter(cmd, "@id", ampSubscriptionId);
        });
    }

    public async Task<List<UsageLedgerRow>> GetRecentAsync(string statusFilter = null, int top = 200)
    {
        // Only the known status vocabulary is ever interpolated-free filtered; anything
        // else means "no filter".
        var status = ValidStatuses.FirstOrDefault(s =>
            string.Equals(s, statusFilter?.Trim(), StringComparison.OrdinalIgnoreCase));

        var sql = $@"{TableGuard}
SELECT TOP (@top) {SelectColumns}
FROM dbo.UsageLedger
{(status != null ? "WHERE Status = @status" : string.Empty)}
ORDER BY CreatedUtc DESC;";

        return await this.QueryRowsAsync(sql, cmd =>
        {
            AddParameter(cmd, "@top", top);
            if (status != null)
            {
                AddParameter(cmd, "@status", status);
            }
        });
    }

    public async Task<UsageLedgerHealth> GetHealthAsync()
    {
        var sql = $@"{TableGuard}
SELECT
    COUNT(CASE WHEN Status = 'pending'  THEN 1 END) AS Pending,
    COUNT(CASE WHEN Status = 'pending'
                AND EffectiveStartUtc < DATEADD(HOUR,-18,SYSUTCDATETIME())
               THEN 1 END)                          AS PendingNearCliff,
    COUNT(CASE WHEN Status = 'accepted'  THEN 1 END) AS Accepted,
    COUNT(CASE WHEN Status = 'duplicate' THEN 1 END) AS Duplicate,
    COUNT(CASE WHEN Status = 'expired'   THEN 1 END) AS Expired,
    COUNT(CASE WHEN Status = 'zero'      THEN 1 END) AS Zero,
    COUNT(CASE WHEN Status = 'skipped'   THEN 1 END) AS Skipped
FROM dbo.UsageLedger;";

        try
        {
            var connection = await this.GetOpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();

            // When the table guard fires there is NO result set at all (not an empty
            // one): Read() returns false and the correct answer is all zeros.
            if (!await reader.ReadAsync())
            {
                return new UsageLedgerHealth();
            }

            return new UsageLedgerHealth
            {
                Pending = reader.GetInt32(0),
                PendingNearCliff = reader.GetInt32(1),
                Accepted = reader.GetInt32(2),
                Duplicate = reader.GetInt32(3),
                Expired = reader.GetInt32(4),
                Zero = reader.GetInt32(5),
                Skipped = reader.GetInt32(6),
            };
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            return new UsageLedgerHealth();
        }
    }

    public async Task<bool> TableExistsAsync()
    {
        var connection = await this.GetOpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID('dbo.UsageLedger','U') IS NULL THEN 0 ELSE 1 END;";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) == 1;
    }

    private async Task<List<UsageLedgerRow>> QueryRowsAsync(string sql, Action<DbCommand> addParameters)
    {
        var rows = new List<UsageLedgerRow>();
        try
        {
            var connection = await this.GetOpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            addParameters(command);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(ReadRow(reader));
            }
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // Invalid object name: table not created yet — belt and braces behind the
            // OBJECT_ID guard. "No data" is the correct answer.
        }

        return rows;
    }

    private async Task<DbConnection> GetOpenConnectionAsync()
    {
        var connection = this.context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static UsageLedgerRow ReadRow(DbDataReader reader) => new UsageLedgerRow
    {
        Id = reader.GetInt64(0),
        AmpSubscriptionId = reader.GetGuid(1),
        DimensionId = reader.GetString(2),
        PlanId = reader.IsDBNull(3) ? null : reader.GetString(3),
        TenantId = reader.GetGuid(4),
        PeriodStartUtc = reader.GetDateTime(5),
        PeriodEndUtc = reader.GetDateTime(6),
        EffectiveStartUtc = reader.GetDateTime(7),
        ActiveUserCount = reader.IsDBNull(8) ? null : reader.GetInt32(8),
        ThresholdApplied = reader.IsDBNull(9) ? null : reader.GetInt32(9),
        WindowStartUtc = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
        WindowEndUtc = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
        SnapshotDateUtc = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
        SnapshotRunMode = reader.IsDBNull(13) ? null : reader.GetByte(13),
        SnapshotComputedUtc = reader.IsDBNull(14) ? null : reader.GetDateTime(14),
        Units = reader.GetInt32(15),
        Status = reader.GetString(16),
        SkipReason = reader.IsDBNull(17) ? null : reader.GetString(17),
        Attempts = reader.GetInt32(18),
        LastAttemptUtc = reader.IsDBNull(19) ? null : reader.GetDateTime(19),
        LastResponse = reader.IsDBNull(20) ? null : reader.GetString(20),
        MsftUsageEventId = reader.IsDBNull(21) ? null : reader.GetString(21),
        AcceptedUtc = reader.IsDBNull(22) ? null : reader.GetDateTime(22),
        CreatedUtc = reader.GetDateTime(23),
    };
}
