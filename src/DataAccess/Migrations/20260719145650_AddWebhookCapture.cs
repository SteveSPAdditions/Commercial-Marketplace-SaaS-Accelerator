using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketplace.SaaS.Accelerator.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookCapture",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CapturedUtc = table.Column<DateTime>(type: "datetime", nullable: false),
                    Action = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    ResultStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookCapture", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookCapture_CapturedUtc",
                table: "WebhookCapture",
                column: "CapturedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookCapture_SubscriptionId",
                table: "WebhookCapture",
                column: "SubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookCapture");
        }
    }
}
