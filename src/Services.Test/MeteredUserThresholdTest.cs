using System;
using System.Text.Json;
using System.Threading.Tasks;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Marketplace.SaaS.Accelerator.Services.Test;

/// <summary>
/// Metered user threshold (N) capture + tenantregions-refresh pipeline:
///  - MeteredPlanGuard: the activation gate the AdminSite SubscriptionOperation action consults
///    (private-offer plan without N blocks activation; public allowlist plans never require N).
///  - SubscriptionSignalService: every signal payload carries the tenantregions trio
///    (saasSubscriptionId, meteredUserThreshold, marketplaceTermStartUtc) so the three values
///    are refreshed by the same events and go stale together or not at all.
///  - SubscriptionTermRefreshService: the live-pull term refresh the activation / ChangePlan /
///    Renew paths run before signaling (the Renew webhook payload carries no term data).
/// </summary>
[TestClass]
public class MeteredUserThresholdTest
{
    private const string DefaultAllowlist = SaaSApiClientConfiguration.PublicPlanIdsDefault;

    // ---------------------------------------------------------------------------------------
    // MeteredPlanGuard -- the activation gate
    // ---------------------------------------------------------------------------------------

    [TestMethod]
    public void PrivatePlanWithoutThresholdBlocksActivation()
    {
        Assert.IsTrue(MeteredPlanGuard.BlocksActivation("contoso-enterprise-500", DefaultAllowlist, null));
    }

    [TestMethod]
    public void PrivatePlanWithNegativeThresholdBlocksActivation()
    {
        Assert.IsTrue(MeteredPlanGuard.BlocksActivation("contoso-enterprise-500", DefaultAllowlist, -1));
    }

    [TestMethod]
    public void PrivatePlanWithThresholdActivates()
    {
        Assert.IsFalse(MeteredPlanGuard.BlocksActivation("contoso-enterprise-500", DefaultAllowlist, 500));
    }

    [TestMethod]
    public void PrivatePlanWithExplicitZeroThresholdActivates()
    {
        // 0 is a valid explicit value for a private plan; only ABSENT blocks.
        Assert.IsFalse(MeteredPlanGuard.BlocksActivation("contoso-enterprise-500", DefaultAllowlist, 0));
    }

    [TestMethod]
    public void AllowlistedPlansActivateWithoutThreshold()
    {
        foreach (var planId in new[] { "free-trial", "standard-monthly", "standard-annual" })
        {
            Assert.IsTrue(MeteredPlanGuard.IsPublicPlan(planId, DefaultAllowlist), $"{planId} should be public");
            Assert.IsFalse(MeteredPlanGuard.RequiresThreshold(planId, DefaultAllowlist), $"{planId} should not require N");
            Assert.IsFalse(MeteredPlanGuard.BlocksActivation(planId, DefaultAllowlist, null), $"{planId} should activate without N");
        }
    }

    [TestMethod]
    public void AllowlistComesFromConfigurationNotCode()
    {
        // A rebuilt offer's plan ids arrive via configuration; the guard honours whatever csv it is given.
        Assert.IsTrue(MeteredPlanGuard.IsPublicPlan("rau-standard-m", "rau-trial, rau-standard-m ,rau-annual"));
        Assert.IsFalse(MeteredPlanGuard.IsPublicPlan("standard-monthly", "rau-trial,rau-standard-m,rau-annual"));
    }

    [TestMethod]
    public void PlanMatchingIsExactAndCaseSensitive()
    {
        // Partner Center ids must match verbatim. A case mismatch fails SAFE: the plan
        // classifies as private and activation demands N, rather than billing with none.
        Assert.IsFalse(MeteredPlanGuard.IsPublicPlan("Standard-Monthly", DefaultAllowlist));
        Assert.IsFalse(MeteredPlanGuard.IsPublicPlan("standard", DefaultAllowlist));
    }

    [TestMethod]
    public void UnknownOrEmptyPlanIdClassifiesAsPrivate()
    {
        Assert.IsFalse(MeteredPlanGuard.IsPublicPlan(null, DefaultAllowlist));
        Assert.IsFalse(MeteredPlanGuard.IsPublicPlan(string.Empty, DefaultAllowlist));
        Assert.IsTrue(MeteredPlanGuard.BlocksActivation(null, DefaultAllowlist, null));
    }

    // ---------------------------------------------------------------------------------------
    // SubscriptionSignalService -- the tenantregions trio rides every signal
    // ---------------------------------------------------------------------------------------

    private static readonly Guid SubscriptionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid TenantId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTime TermStart = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

    private static (SubscriptionSignalService service, Func<NotificationOutbox> enqueued) BuildSignalService(
        Subscriptions subscription, SubscriptionTenantConsent consent)
    {
        NotificationOutbox captured = null;

        var subscriptionsRepo = new Mock<ISubscriptionsRepository>();
        subscriptionsRepo.Setup(x => x.GetById(SubscriptionId, It.IsAny<bool>())).Returns(subscription);

        var outboxRepo = new Mock<INotificationOutboxRepository>();
        outboxRepo.Setup(x => x.GetByIdempotencyKey(It.IsAny<string>())).Returns((NotificationOutbox)null);
        outboxRepo.Setup(x => x.Enqueue(It.IsAny<NotificationOutbox>()))
            .Callback<NotificationOutbox>(e => captured = e)
            .Returns(1);

        var consentRepo = new Mock<ISubscriptionTenantConsentRepository>();
        consentRepo.Setup(x => x.GetByAmpSubscriptionId(SubscriptionId)).Returns(consent);

        var contextMock = new Mock<SaasKitContext>();
        contextMock.Setup(x => x.SaveChanges()).Returns(1);

        var provider = new Mock<IServiceProvider>();
        provider.Setup(x => x.GetService(typeof(ISubscriptionsRepository))).Returns(subscriptionsRepo.Object);
        provider.Setup(x => x.GetService(typeof(INotificationOutboxRepository))).Returns(outboxRepo.Object);
        provider.Setup(x => x.GetService(typeof(ISubscriptionTenantConsentRepository))).Returns(consentRepo.Object);
        provider.Setup(x => x.GetService(typeof(SaasKitContext))).Returns(contextMock.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(x => x.CreateScope()).Returns(scope.Object);

        var service = new SubscriptionSignalService(scopeFactory.Object, NullLogger<SubscriptionSignalService>.Instance);
        return (service, () => captured);
    }

    private static Subscriptions BuildSubscription() => new Subscriptions
    {
        AmpsubscriptionId = SubscriptionId,
        AmpplanId = "contoso-enterprise-500",
        SubscriptionStatus = "Subscribed",
        PurchaserTenantId = TenantId,
        StartDate = TermStart,
    };

    [TestMethod]
    public void SignalPayloadCarriesAllThreeTenantRegionsValues()
    {
        // "A ChangePlan webhook updates all three tenantregions values": the signal the webhook
        // handler enqueues must carry the subscription id, N, and the term start together.
        var consent = new SubscriptionTenantConsent
        {
            Id = 1,
            AmpSubscriptionId = SubscriptionId,
            TenantId = TenantId,
            MeteredUserThreshold = 500,
        };
        var (service, enqueued) = BuildSignalService(BuildSubscription(), consent);

        service.EnqueueSubscriptionSignal(SubscriptionId, "PlanChanged", Guid.NewGuid());

        var entry = enqueued();
        Assert.IsNotNull(entry, "signal must be enqueued");
        Assert.AreEqual("PlanChanged", entry.EventType);

        using var doc = JsonDocument.Parse(entry.EventJson);
        var root = doc.RootElement;
        Assert.AreEqual(SubscriptionId, root.GetProperty("saasSubscriptionId").GetGuid());
        Assert.AreEqual(500, root.GetProperty("meteredUserThreshold").GetInt32());
        Assert.AreEqual(TermStart, root.GetProperty("marketplaceTermStartUtc").GetDateTime());
    }

    [TestMethod]
    public void ActivatedSignalCarriesCapturedThreshold()
    {
        // "Activation with N persists it to both SubscriptionTenantConsent and tenantregions":
        // the controller saves N to the consent row BEFORE activation; the "Activated" signal
        // then reads that row and carries N to the regional tenantregions rows.
        var consent = new SubscriptionTenantConsent
        {
            Id = 1,
            AmpSubscriptionId = SubscriptionId,
            TenantId = TenantId,
            MeteredUserThreshold = 250,
        };
        var (service, enqueued) = BuildSignalService(BuildSubscription(), consent);

        service.EnqueueSubscriptionSignal(SubscriptionId, "Activated", Guid.Empty);

        var entry = enqueued();
        Assert.IsNotNull(entry);
        using var doc = JsonDocument.Parse(entry.EventJson);
        Assert.AreEqual(250, doc.RootElement.GetProperty("meteredUserThreshold").GetInt32());
    }

    [TestMethod]
    public void SignalWithoutConsentRowCarriesNullThreshold()
    {
        // Public plans never capture N; the payload carries null (receiver: leave unchanged),
        // NEVER a defaulted 0-as-guess and never a crash.
        var (service, enqueued) = BuildSignalService(BuildSubscription(), consent: null);

        service.EnqueueSubscriptionSignal(SubscriptionId, "Renewed", Guid.NewGuid());

        var entry = enqueued();
        Assert.IsNotNull(entry);
        using var doc = JsonDocument.Parse(entry.EventJson);
        Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("meteredUserThreshold").ValueKind);
        Assert.AreEqual(TermStart, doc.RootElement.GetProperty("marketplaceTermStartUtc").GetDateTime());
    }

    // ---------------------------------------------------------------------------------------
    // SubscriptionTermRefreshService -- Renew moves MarketplaceTermStartUtc
    // ---------------------------------------------------------------------------------------

    [TestMethod]
    public async Task RenewRefreshesTermStartFromLivePull()
    {
        // On an annual renewal Microsoft starts a new term a year on. The webhook payload has no
        // term data, so the handler live-pulls and persists the new dates -- which the signal
        // then carries as marketplaceTermStartUtc.
        var newStart = new DateTimeOffset(2027, 7, 15, 0, 0, 0, TimeSpan.Zero);
        var newEnd = new DateTimeOffset(2028, 7, 15, 0, 0, 0, TimeSpan.Zero);

        var api = new Mock<IFulfillmentApiService>();
        api.Setup(x => x.GetSubscriptionByIdAsync(SubscriptionId)).ReturnsAsync(new SubscriptionResult
        {
            Id = SubscriptionId,
            PlanId = "annual",
            Term = new TermResult { TermUnit = TermUnitEnum.P1Y, StartDate = newStart, EndDate = newEnd },
        });

        var subscriptionsRepo = new Mock<ISubscriptionsRepository>();

        var refreshed = await new SubscriptionTermRefreshService(api.Object, subscriptionsRepo.Object)
            .RefreshTermAsync(SubscriptionId);

        Assert.IsTrue(refreshed);
        subscriptionsRepo.Verify(
            x => x.UpdateTermForSubscription(SubscriptionId, "P1Y", newStart.UtcDateTime, newEnd.UtcDateTime),
            Times.Once);
    }

    [TestMethod]
    public async Task TermRefreshFailureIsNonFatalAndWritesNothing()
    {
        var api = new Mock<IFulfillmentApiService>();
        api.Setup(x => x.GetSubscriptionByIdAsync(SubscriptionId)).ThrowsAsync(new Exception("API down"));

        var subscriptionsRepo = new Mock<ISubscriptionsRepository>();

        var refreshed = await new SubscriptionTermRefreshService(api.Object, subscriptionsRepo.Object)
            .RefreshTermAsync(SubscriptionId);

        Assert.IsFalse(refreshed);
        subscriptionsRepo.Verify(
            x => x.UpdateTermForSubscription(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()),
            Times.Never);
    }
}
