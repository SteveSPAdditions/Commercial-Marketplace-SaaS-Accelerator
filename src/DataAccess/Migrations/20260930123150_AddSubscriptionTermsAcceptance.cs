using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketplace.SaaS.Accelerator.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionTermsAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubscriptionTermsAcceptance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AmpSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcceptedUtc = table.Column<DateTime>(type: "datetime", nullable: false),
                    AcceptedByUpn = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    AcceptedByObjectId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    AcceptedByDisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    MicrosoftContractAccepted = table.Column<bool>(type: "bit", nullable: false),
                    MicrosoftContractUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MicrosoftContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PublisherAmendmentAccepted = table.Column<bool>(type: "bit", nullable: false),
                    PublisherAmendmentTitle = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PublisherAmendmentUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PublisherAmendmentVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Source = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionTermsAcceptance", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTermsAcceptance_AmpSubscriptionId",
                table: "SubscriptionTermsAcceptance",
                column: "AmpSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTermsAcceptance_TenantId",
                table: "SubscriptionTermsAcceptance",
                column: "TenantId");

            // Terms gate knobs (TermsAcceptanceService). Publisher-editable in AdminSite > Application Config.
            // The gate is ON by default and FAILS CLOSED while either document URL is blank, so set the
            // URLs (and, ideally, versions) before customers arrive. The amendment ships as a page on the
            // customer portal (wwwroot/legal) because the Marketplace only offers it as an HTML download.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'TermsAcceptanceRequired')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('TermsAcceptanceRequired', 'true', 'Customer portal: require agreement to the Microsoft Standard Contract and the publisher amendment before Setup steps can be actioned');
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'TermsMicrosoftContractTitle')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('TermsMicrosoftContractTitle', 'Microsoft Standard Contract for the Microsoft commercial marketplace', 'Terms gate: display title of the Microsoft contract');
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'TermsMicrosoftContractUrl')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('TermsMicrosoftContractUrl', 'https://go.microsoft.com/fwlink/?linkid=2041178', 'Terms gate: link to the Microsoft Standard Contract the customer must agree to');
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'TermsMicrosoftContractVersion')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('TermsMicrosoftContractVersion', '', 'Terms gate: version label of the Microsoft Standard Contract recorded on acceptance (optional)');
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'TermsPublisherAmendmentTitle')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('TermsPublisherAmendmentTitle', 'SP Additions Ltd amendment to the Standard Contract', 'Terms gate: display title of the publisher amendment');
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'TermsPublisherAmendmentUrl')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('TermsPublisherAmendmentUrl', '/legal/amendment-v1.html', 'Terms gate: link to the publisher amendment (site-relative path served by the customer portal, or an absolute URL)');
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'TermsPublisherAmendmentVersion')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('TermsPublisherAmendmentVersion', '1.0', 'Terms gate: version label of the publisher amendment recorded on acceptance');
IF NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationConfiguration] WHERE [Name] = 'IsEmailEnabledForTermsAcceptance')
    INSERT INTO [dbo].[ApplicationConfiguration] ([Name], [Value], [Description])
    VALUES ('IsEmailEnabledForTermsAcceptance', 'true', 'Email the customer a confirmation (with links to the documents) when they accept the terms');
");

            // Confirmation email. Placeholders: ****SubscriptionName**** ****SubscriptionId**** ****AcceptedBy****
            // ****AcceptedUtc**** ****MicrosoftContractUrl**** ****MicrosoftContractVersion****
            // ****PublisherAmendmentTitle**** ****PublisherAmendmentUrl**** ****PublisherAmendmentVersion****.
            // Sent To the person who accepted; set CC/BCC on the template to keep a publisher copy.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM [dbo].[EmailTemplate] WHERE [Status] = 'TermsAccepted')
    INSERT INTO [dbo].[EmailTemplate] ([Status], [Description], [InsertDate], [TemplateBody], [Subject], [isActive])
    VALUES (N'TermsAccepted', N'Sent to the customer when they accept the Marketplace terms and publisher amendment on the customer portal', GETDATE(),
    N'<html><head><meta http-equiv=""Content-Type"" content=""text/html; charset=UTF-8""/></head><body style=""font-family:Segoe UI,Arial,sans-serif;color:#323130;font-size:14px;line-height:1.5""><h2 style=""font-weight:600"">Thank you &mdash; your agreement has been recorded</h2><p>This confirms that <strong>****AcceptedBy****</strong> agreed to the following terms for the subscription <strong>****SubscriptionName****</strong> on <strong>****AcceptedUtc****</strong>.</p><ul><li><a href=""****MicrosoftContractUrl****"">Microsoft Standard Contract for the Microsoft commercial marketplace</a> ****MicrosoftContractVersion****</li><li><a href=""****PublisherAmendmentUrl****"">****PublisherAmendmentTitle****</a> ****PublisherAmendmentVersion****</li></ul><p>Please keep this email for your records. You can now continue with the remaining setup steps on the customer portal.</p><p style=""color:#605e5c;font-size:12px"">Subscription id: ****SubscriptionId****</p></body></html>',
    N'Your agreement to the terms for ****SubscriptionName**** has been recorded', N'True');
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionTermsAcceptance");

            migrationBuilder.Sql(@"
DELETE FROM [dbo].[EmailTemplate] WHERE [Status] = 'TermsAccepted';
DELETE FROM [dbo].[ApplicationConfiguration] WHERE [Name] IN (
    'TermsAcceptanceRequired', 'TermsMicrosoftContractTitle', 'TermsMicrosoftContractUrl', 'TermsMicrosoftContractVersion',
    'TermsPublisherAmendmentTitle', 'TermsPublisherAmendmentUrl', 'TermsPublisherAmendmentVersion', 'IsEmailEnabledForTermsAcceptance');
");
        }
    }
}
