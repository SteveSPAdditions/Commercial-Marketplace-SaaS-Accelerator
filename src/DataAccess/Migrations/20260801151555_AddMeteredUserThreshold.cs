using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketplace.SaaS.Accelerator.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddMeteredUserThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MeteredUserThreshold",
                table: "SubscriptionTenantConsent",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MeteredUserThreshold",
                table: "SubscriptionTenantConsent");
        }
    }
}
