// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Marketplace.SaaS.Accelerator.CustomerSite.Filters;

/// <summary>
/// Turns an antiforgery validation failure into something a customer can act on.
///
/// The global <see cref="AutoValidateAntiforgeryTokenAttribute"/> answers a failed check with a
/// bare, empty HTTP 400, which the browser renders as "This page isn't working". In practice the
/// failure is almost always benign: the customer left a page (typically Setup) open past the
/// 60-minute sign-in, or across a site restart, and then submitted a form whose token no longer
/// matches. Observed 2026-10-01 on the Setup terms step.
///
/// Registered as an always-run result filter because the antiforgery filter short-circuits at the
/// authorization stage, which ordinary result filters never see. For a normal form post we redirect
/// back to the page the form came from (same-origin Referer, else the site root) with a flash
/// message that both the Setup page and the subscriptions list display. For the inline Setup panel's
/// fetch() calls we return the same { requiresRedirect, url } JSON that setup-inline.js already
/// handles, so the panel navigates as a full page instead of blanking.
/// </summary>
public class AntiforgeryFailureRedirectFilter : IAlwaysRunResultFilter
{
    public const string Message = "Your sign-in had expired, so that action was not applied. Please try again.";

    private readonly ITempDataDictionaryFactory tempDataFactory;

    public AntiforgeryFailureRedirectFilter(ITempDataDictionaryFactory tempDataFactory)
    {
        this.tempDataFactory = tempDataFactory;
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not IAntiforgeryValidationFailedResult)
        {
            return;
        }

        var http = context.HttpContext;
        var target = SameOriginReferer(http.Request) ?? "/";

        // Flash for whichever page we land on: Setup/Index reads FlashMessage + FlashIsError,
        // Home/Subscriptions reads ErrorMsg.
        var tempData = this.tempDataFactory.GetTempData(http);
        tempData["FlashMessage"] = Message;
        tempData["FlashIsError"] = true;
        tempData["ErrorMsg"] = Message;

        var isAjax = string.Equals(http.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
        context.Result = isAjax
            ? new JsonResult(new { requiresRedirect = true, url = target })
            : new RedirectResult(target);
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    /// <summary>The Referer when it is on this site, else null. Never redirect off-site on a bad token.</summary>
    private static string SameOriginReferer(HttpRequest request)
    {
        var referer = request.Headers.Referer.ToString();
        if (string.IsNullOrWhiteSpace(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var sameOrigin = string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && (uri.IsDefaultPort ? request.Host.Port is null or 80 or 443 : uri.Port == (request.Host.Port ?? uri.Port));

        return sameOrigin ? uri.PathAndQuery : null;
    }
}
