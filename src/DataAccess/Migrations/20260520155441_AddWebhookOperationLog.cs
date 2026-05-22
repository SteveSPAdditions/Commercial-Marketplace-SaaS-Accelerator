using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketplace.SaaS.Accelerator.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookOperationLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookOperationLog",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime", nullable: false),
                    Action = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookOperationLog", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookOperationLog_ReceivedUtc",
                table: "WebhookOperationLog",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookOperationLog_SubscriptionId",
                table: "WebhookOperationLog",
                column: "SubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookOperationLog");
        }
    }
}
