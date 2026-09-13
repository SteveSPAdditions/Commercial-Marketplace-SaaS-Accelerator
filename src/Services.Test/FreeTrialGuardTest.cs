using System;
using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Marketplace.SaaS.Accelerator.Services.Test;

/// <summary>
/// FreeTrialGuard -- the repeat-free-trial auto-activation gate consulted by the CustomerSite
/// portal-entry and Activate paths. Blocks self-service activation of a new free trial when the
/// purchaser has a prior, now-terminated subscription that consumed a full trial window
/// (activated more than 37 days ago) and ended within the 365-day cooldown; early re-subscribes
/// that keep a running trial alive stay self-service.
/// </summary>
[TestClass]
public class FreeTrialGuardTest
{
    private static readonly DateTime Now = new DateTime(2026, 08, 06, 12, 0, 0, DateTimeKind.Utc);

    private static Subscriptions Prior(
        string status,
        int activatedDaysAgo,
        int? endedDaysAgo = null)
    {
        return new Subscriptions
        {
            AmpsubscriptionId = Guid.NewGuid(),
            SubscriptionStatus = status,
            StartDate = Now.AddDays(-activatedDaysAgo),
            CreateDate = Now.AddDays(-activatedDaysAgo),
            ModifyDate = endedDaysAgo.HasValue ? Now.AddDays(-endedDaysAgo.Value) : (DateTime?)null,
            EndDate = endedDaysAgo.HasValue ? Now.AddDays(-endedDaysAgo.Value) : (DateTime?)null,
        };
    }

    [TestMethod]
    public void NoPriorSubscriptionsAllowsAutoActivation()
    {
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, new List<Subscriptions>(), Now));
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, null, Now));
    }

    [TestMethod]
    public void PaidSubscriptionIsNeverBlocked()
    {
        var priors = new List<Subscriptions> { Prior("Unsubscribed", activatedDaysAgo: 60, endedDaysAgo: 20) };
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(false, priors, Now));
    }

    [TestMethod]
    public void EarlyCancelledTrialAllowsRetry()
    {
        // Trial cancelled after 3 days, re-subscribed today: still inside the 37-day window.
        var priors = new List<Subscriptions> { Prior("Unsubscribed", activatedDaysAgo: 3, endedDaysAgo: 1) };
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, priors, Now));
    }

    [TestMethod]
    public void MultipleEarlyRetriesStayAllowed()
    {
        var priors = new List<Subscriptions>
        {
            Prior("Unsubscribed", activatedDaysAgo: 30, endedDaysAgo: 25),
            Prior("Unsubscribed", activatedDaysAgo: 20, endedDaysAgo: 10),
            Prior("Unsubscribed", activatedDaysAgo: 5, endedDaysAgo: 2),
        };
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, priors, Now));
    }

    [TestMethod]
    public void ConsumedTrialWithinCooldownBlocks()
    {
        // Activated ~2 months ago, unsubscribed 20 days ago: full window consumed, cooldown active.
        var priors = new List<Subscriptions> { Prior("Unsubscribed", activatedDaysAgo: 60, endedDaysAgo: 20) };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors, Now));
    }

    [TestMethod]
    public void RetryChainOlderThanWindowBlocks()
    {
        // First trial 40 days back: the 37-day window is exhausted even though the latest
        // attempt is recent.
        var priors = new List<Subscriptions>
        {
            Prior("Unsubscribed", activatedDaysAgo: 40, endedDaysAgo: 37),
            Prior("Unsubscribed", activatedDaysAgo: 35, endedDaysAgo: 2),
        };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors, Now));
    }

    [TestMethod]
    public void ConsumedTrialOutsideCooldownAllows()
    {
        // Ended 13 months ago: cooldown elapsed, trial eligibility resets.
        var priors = new List<Subscriptions> { Prior("Unsubscribed", activatedDaysAgo: 430, endedDaysAgo: 395) };
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, priors, Now));
    }

    [TestMethod]
    public void ActiveSubscriptionDoesNotBlock()
    {
        // A live Subscribed subscription is not a terminated prior (its EndDate is in the future).
        var active = Prior("Subscribed", activatedDaysAgo: 60);
        active.EndDate = Now.AddDays(10);
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, new List<Subscriptions> { active }, Now));
    }

    [TestMethod]
    public void ExpiredTermCountsAsTerminatedEvenWithStaleStatus()
    {
        // Status never flipped to Unsubscribed but the term ended 20 days ago.
        var stale = Prior("Subscribed", activatedDaysAgo: 60, endedDaysAgo: 20);
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, new List<Subscriptions> { stale }, Now));
    }

    [TestMethod]
    public void PendingUnsubscribeCountsAsTerminated()
    {
        var priors = new List<Subscriptions> { Prior("PendingUnsubscribe", activatedDaysAgo: 60, endedDaysAgo: 1) };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors, Now));
    }

    [TestMethod]
    public void TerminatedPriorWithNoDatesFailsSafeAndBlocks()
    {
        var undated = new Subscriptions
        {
            AmpsubscriptionId = Guid.NewGuid(),
            SubscriptionStatus = "Unsubscribed",
        };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, new List<Subscriptions> { undated }, Now));
    }

    [TestMethod]
    public void CustomWindowAndCooldownAreHonoured()
    {
        var priors = new List<Subscriptions> { Prior("Unsubscribed", activatedDaysAgo: 20, endedDaysAgo: 10) };

        // Default 37-day window: a 20-day-old activation is still a retry.
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, priors, Now));

        // Tightened 14-day window: the same prior counts as consumed.
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors, Now, retryWindowDays: 14));

        // Shortened cooldown: a prior that ended 10 days ago falls outside a 5-day cooldown.
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, priors, Now, retryWindowDays: 14, cooldownDays: 5));
    }
}
