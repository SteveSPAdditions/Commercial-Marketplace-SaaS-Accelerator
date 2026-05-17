using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketplace.SaaS.Accelerator.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddSetupUxTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    EventJson = table.Column<string>(type: "varchar(max)", unicode: false, nullable: true),
                    AmpSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime", nullable: false),
                    DeliveredUtc = table.Column<DateTime>(type: "datetime", nullable: true),
                    LastError = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: true),
                    LastResponseSnippet = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true),
                    DeadLettered = table.Column<bool>(type: "bit", nullable: false),
                    LeasedUntilUtc = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionSite",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AmpSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SharePointSiteUrl = table.Column<string>(type: "varchar(500)", unicode: false, maxLength: 500, nullable: true),
                    GraphSiteId = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    CurrentRole = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    PermissionId = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    GrantedUtc = table.Column<DateTime>(type: "datetime", nullable: true),
                    GrantedByUpn = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    DowngradedUtc = table.Column<DateTime>(type: "datetime", nullable: true),
                    FailureReason = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime", nullable: true),
                    ModifiedUtc = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionSite", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionTenantConsent",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AmpSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AzureRegion = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    AzureRegionSelectedUtc = table.Column<DateTime>(type: "datetime", nullable: true),
                    AzureRegionSelectedByUpn = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    TenantRegionsFanOutCompleteUtc = table.Column<DateTime>(type: "datetime", nullable: true),
                    FanOutFailureRegions = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: true),
                    RuntimeAppConsentedUtc = table.Column<DateTime>(type: "datetime", nullable: true),
                    ConsentedByUpn = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    ConsentedByObjectId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime", nullable: true),
                    ModifiedUtc = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionTenantConsent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_AmpSubscriptionId",
                table: "NotificationOutbox",
                column: "AmpSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_IdempotencyKey",
                table: "NotificationOutbox",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_NextAttemptUtc",
                table: "NotificationOutbox",
                column: "NextAttemptUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionSite_AmpSubscriptionId",
                table: "SubscriptionSite",
                column: "AmpSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTenantConsent_AmpSubscriptionId",
                table: "SubscriptionTenantConsent",
                column: "AmpSubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTenantConsent_TenantId",
                table: "SubscriptionTenantConsent",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationOutbox");

            migrationBuilder.DropTable(
                name: "SubscriptionSite");

            migrationBuilder.DropTable(
                name: "SubscriptionTenantConsent");
        }
    }
}
