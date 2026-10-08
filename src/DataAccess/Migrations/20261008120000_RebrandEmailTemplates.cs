using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketplace.SaaS.Accelerator.DataAccess.Migrations
{
    /// <summary>
    /// Replaces the upstream Contoso sample branding baked into the four seeded email bodies
    /// (Subscribed, Unsubscribed, PendingActivation, Failed): the header logo hosted on Microsoft's
    /// GitHub repository and the preheader footer that named Contoso twice and linked to the
    /// saaskitdemoapp sample site. Data-only; no model change, hence no Designer file.
    ///
    /// Each replacement is a plain REPLACE, so a body already edited in the Admin portal is left
    /// alone wherever the sample markup is no longer present. The ApplicationName configuration
    /// value (seeded "Contoso", used in the "Welcome to ..." heading) is deliberately not touched
    /// here: it is also carried on web notifications to the runtime and is set on the Application
    /// Config page.
    /// </summary>
    [DbContext(typeof(SaasKitContext))]
    [Migration("20261008120000_RebrandEmailTemplates")]
    public partial class RebrandEmailTemplates : Migration
    {
        private const string OldLogo = "https://raw.githubusercontent.com/Azure/Commercial-Marketplace-SaaS-Accelerator/main/src/CustomerSite/wwwroot/contoso-sales.png";
        private const string NewLogo = "https://readandunderstoodcdn.blob.core.windows.net/images/Read%20and%20Understood%20-%20MS%20Logo%20-%20CDN%20-%20Sqaure.png";

        // The sample logo is a 300px-wide banner; the replacement is a 300x300 square, so the header
        // image is capped at 120px to keep it a logo rather than a hero. Matched together with the
        // id so no other element's style is affected.
        private const string OldHeaderStyle = "style=\"max-width: 300px; display: block; margin-left: auto; margin-right: auto; padding-top:10px;padding-bottom:10px;\" id=\"headerImage\"";
        private const string NewHeaderStyle = "style=\"max-width: 120px; display: block; margin-left: auto; margin-right: auto; padding-top:10px;padding-bottom:10px;\" id=\"headerImage\"";

        private const string OldContact = "Please contact us at <a href=\"https://saaskitdemoapp.azurewebsites.net/\">Contoso</a>";
        private const string NewContact = "Please contact us at <a href=\"mailto:support@spadditions.zendesk.com\">support@spadditions.zendesk.com</a>";

        private const string OldBrand = "<a href=\"https://saaskitdemoapp.azurewebsites.net/\">Contoso</a>";
        private const string NewBrand = "SP Additions Ltd";

        // The "View Details" button. Once the contact sentence and brand anchor are gone this is the
        // only demo-site href left, so a bare href match is safe. The portal host is not available
        // to a migration (CustomerSiteBaseUrl is an app setting), hence the literal.
        private const string OldCtaHref = "href=\"https://saaskitdemoapp.azurewebsites.net/\"";
        private const string NewCtaHref = "href=\"https://rau-portal.azurewebsites.net/Home/Subscriptions\"";

        private const string Statuses = "('Subscribed', 'Unsubscribed', 'PendingActivation', 'Failed')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters: the contact sentence contains the brand anchor, so it is replaced first,
            // and the bare href only after both anchors are gone.
            migrationBuilder.Sql(
                "UPDATE [dbo].[EmailTemplate] SET [TemplateBody] = "
                + Replace(Replace(Replace(Replace(Replace("[TemplateBody]", OldLogo, NewLogo), OldHeaderStyle, NewHeaderStyle), OldContact, NewContact), OldBrand, NewBrand), OldCtaHref, NewCtaHref)
                + " WHERE [Status] IN " + Statuses + ";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best effort, in reverse: the button href first, then the brand text back to its
            // anchor, then the contact sentence as a whole, then the header style and logo.
            migrationBuilder.Sql(
                "UPDATE [dbo].[EmailTemplate] SET [TemplateBody] = "
                + Replace(Replace(Replace(Replace(Replace("[TemplateBody]", NewCtaHref, OldCtaHref), NewBrand, OldBrand), NewContact, OldContact), NewHeaderStyle, OldHeaderStyle), NewLogo, OldLogo)
                + " WHERE [Status] IN " + Statuses + ";");
        }

        /// <summary>T-SQL REPLACE over a varchar(max) expression with single-quote-escaped literals.</summary>
        private static string Replace(string expression, string oldValue, string newValue)
            => "REPLACE(" + expression + ", '" + Escape(oldValue) + "', '" + Escape(newValue) + "')";

        private static string Escape(string value) => value.Replace("'", "''");
    }
}
