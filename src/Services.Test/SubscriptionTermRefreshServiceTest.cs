using System;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;
using Marketplace.SaaS.Accelerator.Services.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Marketplace.SaaS.Accelerator.Services.Test;

/// <summary>
/// SubscriptionTermRefreshService -- the post-activation / post-webhook live pull of the
/// subscription term. Fulfillment populates the term a moment after Activate returns, so the
/// immediate re-pull can carry a term object with unset (MinValue) dates. Those must never be
/// written: they stamped 0001-01-01 on Subscriptions.StartDate and shipped it to RAU as
/// marketplaceTermStartUtc on the "Activated" signal.
/// </summary>
[TestClass]
public class SubscriptionTermRefreshServiceTest
{
    private static readonly Guid SubscriptionId = Guid.NewGuid();

    private static SubscriptionResult Live(DateTimeOffset start, DateTimeOffset end)
    {
        return new SubscriptionResult
        {
            Id = SubscriptionId,
            Term = new TermResult { TermUnit = TermUnitEnum.P1M, StartDate = start, EndDate = end },
        };
    }

    [TestMethod]
    public async Task PopulatedTermIsWritten()
    {
        var start = new DateTimeOffset(2026, 09, 22, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(1);
        var api = new Mock<IFulfillmentApiService>();
        api.Setup(a => a.GetSubscriptionByIdAsync(SubscriptionId)).ReturnsAsync(Live(start, end));
        var repo = new Mock<ISubscriptionsRepository>();

        var refreshed = await new SubscriptionTermRefreshService(api.Object, repo.Object).RefreshTermAsync(SubscriptionId);

        Assert.IsTrue(refreshed);
        repo.Verify(r => r.UpdateTermForSubscription(SubscriptionId, "P1M", start.UtcDateTime, end.UtcDateTime), Times.Once);
    }

    [TestMethod]
    public async Task UnpopulatedTermIsNotWritten()
    {
        // The activation-time race: term object present, dates still default.
        var api = new Mock<IFulfillmentApiService>();
        api.Setup(a => a.GetSubscriptionByIdAsync(SubscriptionId))
            .ReturnsAsync(Live(DateTimeOffset.MinValue, DateTimeOffset.MinValue));
        var repo = new Mock<ISubscriptionsRepository>();

        var refreshed = await new SubscriptionTermRefreshService(api.Object, repo.Object).RefreshTermAsync(SubscriptionId);

        Assert.IsFalse(refreshed);
        repo.Verify(
            r => r.UpdateTermForSubscription(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()),
            Times.Never);
    }

    [TestMethod]
    public async Task MissingSubscriptionIsNotWritten()
    {
        var api = new Mock<IFulfillmentApiService>();
        api.Setup(a => a.GetSubscriptionByIdAsync(SubscriptionId)).ReturnsAsync((SubscriptionResult)null);
        var repo = new Mock<ISubscriptionsRepository>();

        var refreshed = await new SubscriptionTermRefreshService(api.Object, repo.Object).RefreshTermAsync(SubscriptionId);

        Assert.IsFalse(refreshed);
        repo.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void HasUsableTermRequiresBothDates()
    {
        var real = new DateTimeOffset(2026, 09, 22, 0, 0, 0, TimeSpan.Zero);
        Assert.IsFalse(SubscriptionTermRefreshService.HasUsableTerm(null));
        Assert.IsFalse(SubscriptionTermRefreshService.HasUsableTerm(new TermResult { StartDate = DateTimeOffset.MinValue, EndDate = real }));
        Assert.IsFalse(SubscriptionTermRefreshService.HasUsableTerm(new TermResult { StartDate = real, EndDate = DateTimeOffset.MinValue }));
        Assert.IsTrue(SubscriptionTermRefreshService.HasUsableTerm(new TermResult { StartDate = real, EndDate = real.AddMonths(1) }));
    }

    [TestMethod]
    public void UsableOrNullMasksTheSentinel()
    {
        Assert.IsNull(SubscriptionTermRefreshService.UsableOrNull(null));
        Assert.IsNull(SubscriptionTermRefreshService.UsableOrNull(DateTime.MinValue));
        var real = new DateTime(2026, 09, 22, 0, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(real, SubscriptionTermRefreshService.UsableOrNull(real));
    }
}
