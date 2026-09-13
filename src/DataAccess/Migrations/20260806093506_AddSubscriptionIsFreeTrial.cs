using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketplace.SaaS.Accelerator.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionIsFreeTrial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFreeTrial",
                table: "Subscriptions",
                type: "bit",
                nullable: true);

            // Knobs for the repeat-free-trial auto-activation gate (FreeTrialGuard).
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'FreeTrialRetryWindowDays')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('FreeTrialRetryWindowDays', '37', 'Days from a prior activation within which a re-subscribed free trial still auto-activates (trial length + grace)');
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'FreeTrialCooldownDays')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('FreeTrialCooldownDays', '365', 'Days after a consumed subscription ends during which a new free trial needs manual publisher activation');
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFreeTrial",
                table: "Subscriptions");

            migrationBuilder.Sql(@"
DELETE FROM [dbo].[ApplicationConfiguration] WHERE [Name] IN ('FreeTrialRetryWindowDays', 'FreeTrialCooldownDays');
");
        }
    }
}
