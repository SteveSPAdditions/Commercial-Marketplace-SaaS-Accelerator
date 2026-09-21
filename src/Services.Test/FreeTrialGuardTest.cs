using System;
using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Marketplace.SaaS.Accelerator.Services.Test;

/// <summary>
/// FreeTrialGuard -- the repeat-free-trial auto-activation gate consulted by the CustomerSite
/// portal-entry and Activate paths. One free trial per purchaser, ever: blocks self-service
/// activation of a new free trial when the purchaser has any prior subscription that was (or may
/// have been) a free trial, regardless of its status or age.
/// </summary>
[TestClass]
public class FreeTrialGuardTest
{
    private static Subscriptions Prior(string status, bool? isFreeTrial)
    {
        return new Subscriptions
        {
            AmpsubscriptionId = Guid.NewGuid(),
            SubscriptionStatus = status,
            IsFreeTrial = isFreeTrial,
        };
    }

    [TestMethod]
    public void NoPriorSubscriptionsAllowsAutoActivation()
    {
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, new List<Subscriptions>()));
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, null));
    }

    [TestMethod]
    public void PaidSubscriptionIsNeverBlocked()
    {
        var priors = new List<Subscriptions> { Prior("Unsubscribed", isFreeTrial: true) };
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(false, priors));
    }

    [TestMethod]
    public void PriorUnsubscribedTrialBlocks()
    {
        var priors = new List<Subscriptions> { Prior("Unsubscribed", isFreeTrial: true) };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors));
    }

    [TestMethod]
    public void SameDayCancelAndResubscribeBlocks()
    {
        // The trial that was cancelled minutes ago still counts: there is no retry allowance.
        var prior = Prior("Unsubscribed", isFreeTrial: true);
        prior.StartDate = DateTime.UtcNow.Date;
        prior.ModifyDate = DateTime.UtcNow;
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, new List<Subscriptions> { prior }));
    }

    [TestMethod]
    public void PriorActiveTrialBlocks()
    {
        var priors = new List<Subscriptions> { Prior("Subscribed", isFreeTrial: true) };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors));
    }

    [TestMethod]
    public void PriorPendingUnsubscribeTrialBlocks()
    {
        var priors = new List<Subscriptions> { Prior("PendingUnsubscribe", isFreeTrial: true) };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors));
    }

    [TestMethod]
    public void PriorPaidSubscriptionDoesNotBlock()
    {
        var priors = new List<Subscriptions>
        {
            Prior("Unsubscribed", isFreeTrial: false),
            Prior("Subscribed", isFreeTrial: false),
        };
        Assert.IsFalse(FreeTrialGuard.BlocksAutoActivation(true, priors));
    }

    [TestMethod]
    public void MixedPriorsBlockWhenAnyWasATrial()
    {
        var priors = new List<Subscriptions>
        {
            Prior("Unsubscribed", isFreeTrial: false),
            Prior("Unsubscribed", isFreeTrial: true),
        };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors));
    }

    [TestMethod]
    public void LegacyPriorWithUnknownTrialFlagFailsSafeAndBlocks()
    {
        var priors = new List<Subscriptions> { Prior("Unsubscribed", isFreeTrial: null) };
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, priors));
    }

    [TestMethod]
    public void VeryOldTrialStillBlocks()
    {
        // No cooldown: a trial that ended years ago still consumes the purchaser's one trial.
        var prior = Prior("Unsubscribed", isFreeTrial: true);
        prior.StartDate = new DateTime(2020, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        prior.ModifyDate = new DateTime(2020, 02, 01, 0, 0, 0, DateTimeKind.Utc);
        Assert.IsTrue(FreeTrialGuard.BlocksAutoActivation(true, new List<Subscriptions> { prior }));
    }
}
