using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.Services.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Marketplace.SaaS.Accelerator.Services.Test;

[TestClass]
public class SetupCarryOverServiceTest
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PriorSubscriptionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid NewSubscriptionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private Mock<ISubscriptionTenantConsentRepository> consentRepo;
    private Mock<ISubscriptionSiteRepository> siteRepo;
    private Mock<ISubscriptionsRepository> subscriptionsRepo;
    private List<SubscriptionSite> saved;

    [TestInitialize]
    public void Initialize()
    {
        this.saved = new List<SubscriptionSite>();

        this.consentRepo = new Mock<ISubscriptionTenantConsentRepository>();
        this.consentRepo.Setup(x => x.GetByAmpSubscriptionId(NewSubscriptionId))
            .Returns((SubscriptionTenantConsent)null);
        this.consentRepo.Setup(x => x.GetByTenantId(TenantId))
            .Returns(new SubscriptionTenantConsent
            {
                Id = 7,
                AmpSubscriptionId = PriorSubscriptionId,
                TenantId = TenantId,
                AzureRegion = "UK",
                RuntimeAppConsentedUtc = DateTime.UtcNow.AddDays(-30),
                TeamsActivityAppConsentedUtc = DateTime.UtcNow.AddDays(-30),
            });

        this.siteRepo = new Mock<ISubscriptionSiteRepository>();
        this.siteRepo.Setup(x => x.ListBySubscription(PriorSubscriptionId))
            .Returns(new[]
            {
                new SubscriptionSite
                {
                    Id = 1,
                    AmpSubscriptionId = PriorSubscriptionId,
                    SharePointSiteUrl = "https://contoso.sharepoint.com/sites/hr",
                    GraphSiteId = "contoso.sharepoint.com,site-guid,web-guid",
                    Status = "Granted",
                    CurrentRole = "manage",
                    PermissionId = "perm-1",
                    GrantedUtc = DateTime.UtcNow.AddDays(-20),
                    GrantedByUpn = "admin@contoso.com",
                },
            });
        this.siteRepo.Setup(x => x.ListBySubscription(NewSubscriptionId))
            .Returns(Array.Empty<SubscriptionSite>());
        this.siteRepo.Setup(x => x.Save(It.IsAny<SubscriptionSite>()))
            .Callback<SubscriptionSite>(s => this.saved.Add(s))
            .Returns(1);

        this.subscriptionsRepo = new Mock<ISubscriptionsRepository>();
        this.subscriptionsRepo.Setup(x => x.GetById(PriorSubscriptionId, It.IsAny<bool>()))
            .Returns(new Subscriptions
            {
                AmpsubscriptionId = PriorSubscriptionId,
                SubscriptionStatus = "Unsubscribed",
                PurchaserTenantId = TenantId,
            });
    }

    private SetupCarryOverService Build() => new SetupCarryOverService(
        this.consentRepo.Object,
        this.siteRepo.Object,
        this.subscriptionsRepo.Object,
        NullLogger<SetupCarryOverService>.Instance);

    [TestMethod]
    public void CarriesSiteUrlAndGraphIdForward()
    {
        var count = this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.AreEqual(1, count);
        var site = this.saved.Single();
        Assert.AreEqual(NewSubscriptionId, site.AmpSubscriptionId);
        Assert.AreEqual("https://contoso.sharepoint.com/sites/hr", site.SharePointSiteUrl);
        Assert.AreEqual("contoso.sharepoint.com,site-guid,web-guid", site.GraphSiteId);
    }

    [TestMethod]
    public void DoesNotCarryGrantState()
    {
        this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId);

        var site = this.saved.Single();
        Assert.AreEqual("Pending", site.Status);
        Assert.IsNull(site.CurrentRole);
        Assert.IsNull(site.PermissionId);
        Assert.IsNull(site.GrantedUtc);
        Assert.IsNull(site.GrantedByUpn);
    }

    [TestMethod]
    public void SkipsWhenPriorSubscriptionIsStillLive()
    {
        // A tenant holding two concurrent subscriptions must not have one's site list
        // merged into the other.
        this.subscriptionsRepo.Setup(x => x.GetById(PriorSubscriptionId, It.IsAny<bool>()))
            .Returns(new Subscriptions
            {
                AmpsubscriptionId = PriorSubscriptionId,
                SubscriptionStatus = "Subscribed",
                PurchaserTenantId = TenantId,
            });

        var count = this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, this.saved.Count);
    }

    [TestMethod]
    public void SkipsWhenSetupHasAlreadyStartedForTheNewSubscription()
    {
        this.consentRepo.Setup(x => x.GetByAmpSubscriptionId(NewSubscriptionId))
            .Returns(new SubscriptionTenantConsent { Id = 9, AmpSubscriptionId = NewSubscriptionId });

        var count = this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, this.saved.Count);
    }

    [TestMethod]
    public void SkipsWhenTenantIsUnknown()
    {
        var count = this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, Guid.Empty);

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, this.saved.Count);
    }

    [TestMethod]
    public void DoesNotDuplicateASiteAlreadyOnTheNewSubscription()
    {
        this.siteRepo.Setup(x => x.ListBySubscription(NewSubscriptionId))
            .Returns(new[]
            {
                new SubscriptionSite
                {
                    Id = 5,
                    AmpSubscriptionId = NewSubscriptionId,
                    // Same site, different casing.
                    SharePointSiteUrl = "https://Contoso.sharepoint.com/sites/HR",
                    Status = "Pending",
                },
            });

        var count = this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, this.saved.Count);
    }

    [TestMethod]
    public void SwallowsRepositoryFailuresSoSetupStillLoads()
    {
        this.siteRepo.Setup(x => x.ListBySubscription(PriorSubscriptionId))
            .Throws(new InvalidOperationException("db down"));

        var count = this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.AreEqual(0, count);
    }
}
