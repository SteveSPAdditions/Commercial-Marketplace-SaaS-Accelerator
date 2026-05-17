// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System.Web;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace Marketplace.SaaS.Accelerator.AdminSite.Controllers;

/// <summary>
/// Diagnostics for the notification outbox. Lists dead-lettered rows and
/// supports manual retry.
/// </summary>
public class OutboxController : BaseController
{
    private readonly INotificationOutboxRepository outboxRepo;
    private readonly SaaSClientLogger<OutboxController> logger;

    public OutboxController(
        INotificationOutboxRepository outboxRepo,
        IApplicationConfigRepository applicationConfigRepository,
        IAppVersionService appVersionService,
        SaaSClientLogger<OutboxController> logger) : base(applicationConfigRepository, appVersionService)
    {
        this.outboxRepo = outboxRepo;
        this.logger = logger;
    }

    public IActionResult Index()
    {
        if (this.User?.Identity?.IsAuthenticated != true)
        {
            return this.RedirectToAction("Index", "Home");
        }
        var rows = this.outboxRepo.ListDeadLettered();
        return this.View(rows);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Retry(int id)
    {
        if (this.User?.Identity?.IsAuthenticated != true)
        {
            return this.RedirectToAction("Index", "Home");
        }
        this.outboxRepo.Retry(id);
        this.logger.Info(HttpUtility.HtmlEncode($"Outbox row {id} reset for retry by {this.CurrentUserEmailAddress}"));
        return this.RedirectToAction(nameof(this.Index));
    }
}
