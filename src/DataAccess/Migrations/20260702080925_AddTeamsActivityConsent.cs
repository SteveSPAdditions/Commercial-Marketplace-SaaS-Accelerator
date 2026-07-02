using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketplace.SaaS.Accelerator.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamsActivityConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TeamsActivityAppConsentedUtc",
                table: "SubscriptionTenantConsent",
                type: "datetime",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamsActivityConsentedByObjectId",
                table: "SubscriptionTenantConsent",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamsActivityConsentedByUpn",
                table: "SubscriptionTenantConsent",
                type: "varchar(255)",
                unicode: false,
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeamsActivityAppConsentedUtc",
                table: "SubscriptionTenantConsent");

            migrationBuilder.DropColumn(
                name: "TeamsActivityConsentedByObjectId",
                table: "SubscriptionTenantConsent");

            migrationBuilder.DropColumn(
                name: "TeamsActivityConsentedByUpn",
                table: "SubscriptionTenantConsent");
        }
    }
}
