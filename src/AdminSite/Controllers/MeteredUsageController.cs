using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace Marketplace.SaaS.Accelerator.AdminSite.Controllers;

/// <summary>
/// Publisher-facing display of the UsageLedger — the billing ledger written by the
/// external RauMetering pipeline. Read-only observability: the emergency lever for
/// manual emission remains the per-subscription Record Usage page.
/// </summary>
[ServiceFilter(typeof(KnownUserAttribute))]
public class MeteredUsageController : BaseController
{
    private readonly SaaSClientLogger<MeteredUsageController> logger;

    private readonly IUsageLedgerReadRepository usageLedgerReadRepository;

    public MeteredUsageController(
        IUsageLedgerReadRepository usageLedgerReadRepository,
        IApplicationConfigRepository applicationConfigRepository,
        IAppVersionService appVersionService,
        SaaSClientLogger<MeteredUsageController> logger) : base(applicationConfigRepository, appVersionService)
    {
        this.usageLedgerReadRepository = usageLedgerReadRepository;
        this.logger = logger;
    }

    public async Task<IActionResult> Index(string status)
    {
        this.logger.Info(HttpUtility.HtmlEncode($"MeteredUsage Controller / Index status:{status}"));
        try
        {
            var tableExists = await this.usageLedgerReadRepository.TableExistsAsync();
            this.ViewBag.TableExists = tableExists;
            this.ViewBag.StatusFilter = status;
            this.ViewBag.Health = tableExists
                ? await this.usageLedgerReadRepository.GetHealthAsync()
                : new UsageLedgerHealth();

            var rows = tableExists
                ? await this.usageLedgerReadRepository.GetRecentAsync(status, 200)
                : new List<UsageLedgerRow>();
            return this.View(rows);
        }
        catch (Exception ex)
        {
            this.logger.LogError($"Message:{ex.Message} :: {ex.InnerException}");
            return this.View("Error", ex);
        }
    }
}
