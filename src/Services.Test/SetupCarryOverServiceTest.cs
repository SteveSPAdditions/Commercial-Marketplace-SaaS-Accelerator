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
    private List<SubscriptionTenantConsent> savedConsents;
    private SubscriptionTenantConsent priorConsent;

    [TestInitialize]
    public void Initialize()
    {
        this.saved = new List<SubscriptionSite>();
        this.savedConsents = new List<SubscriptionTenantConsent>();

        this.priorConsent = new SubscriptionTenantConsent
        {
            Id = 7,
            AmpSubscriptionId = PriorSubscriptionId,
            TenantId = TenantId,
            AzureRegion = "UK",
            AzureRegionSelectedByUpn = "admin@contoso.com",
            TenantRegionsFanOutCompleteUtc = DateTime.UtcNow.AddDays(-30),
            RuntimeAppConsentedUtc = DateTime.UtcNow.AddDays(-30),
            TeamsActivityAppConsentedUtc = DateTime.UtcNow.AddDays(-30),
            MeteredUserThreshold = 250,
        };

        this.consentRepo = new Mock<ISubscriptionTenantConsentRepository>();
        this.consentRepo.Setup(x => x.GetByAmpSubscriptionId(NewSubscriptionId))
            .Returns((SubscriptionTenantConsent)null);
        this.consentRepo.Setup(x => x.GetPreviousByTenantId(TenantId, NewSubscriptionId))
            .Returns(() => this.priorConsent);
        this.consentRepo.Setup(x => x.Save(It.IsAny<SubscriptionTenantConsent>()))
            .Callback<SubscriptionTenantConsent>(c => this.savedConsents.Add(c))
            .Returns(9);

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
    public void StillSeedsSitesWhenTheNewSubscriptionAlreadyHasAConsentRow()
    {
        // The consent row is now minted at activation (region carry-over), before Setup is ever
        // opened -- so its presence no longer means "Setup already ran". Only site rows do.
        this.consentRepo.Setup(x => x.GetByAmpSubscriptionId(NewSubscriptionId))
            .Returns(new SubscriptionTenantConsent { Id = 9, AmpSubscriptionId = NewSubscriptionId, AzureRegion = "UK" });

        var count = this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.AreEqual(1, count);
        Assert.AreEqual(1, this.saved.Count);
    }

    [TestMethod]
    public void SkipsWhenTheNewSubscriptionAlreadyHasSites()
    {
        // Any site row on the new subscription -- seeded earlier or added by hand -- means there is
        // progress to protect; the seed must not run twice.
        this.siteRepo.Setup(x => x.ListBySubscription(NewSubscriptionId))
            .Returns(new[]
            {
                new SubscriptionSite
                {
                    Id = 5,
                    AmpSubscriptionId = NewSubscriptionId,
                    SharePointSiteUrl = "https://contoso.sharepoint.com/sites/finance",
                    Status = "Pending",
                },
            });

        var count = this.Build().CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, this.saved.Count);
    }

    [TestMethod]
    public void SkipsWhenThereIsNoPredecessor()
    {
        this.priorConsent = null;

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

    // ------------------------------------------------------------------
    // Region carry-over at activation -- CarryOverRegionFromPreviousSubscription
    //
    // The reconcile snapshot names only subscriptions that have a consent row with a region. A
    // resubscribe that auto-activates and is never opened in Setup would otherwise leave the
    // snapshot pointing at the dead predecessor (seen 2026-09-23: RAU's daily reconcile reverted
    // its TenantRegions row to the Unsubscribed id 18 minutes after the Activated signal).
    // ------------------------------------------------------------------

    [TestMethod]
    public void Region_MintsTheNewSubscriptionsRowWithThePriorRegion()
    {
        var minted = this.Build().CarryOverRegionFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.IsTrue(minted);
        var row = this.savedConsents.Single();
        Assert.AreEqual(NewSubscriptionId, row.AmpSubscriptionId);
        Assert.AreEqual(TenantId, row.TenantId);
        Assert.AreEqual("UK", row.AzureRegion);
        Assert.IsNotNull(row.AzureRegionSelectedUtc);
        Assert.AreEqual(SetupCarryOverService.RegionCarryOverActor, row.AzureRegionSelectedByUpn);
        Assert.IsNotNull(row.TenantRegionsFanOutCompleteUtc, "a carried region counts as fanned out -- RAU already holds the tenant's row");
        Assert.IsNull(row.FanOutFailureRegions);
    }

    [TestMethod]
    public void Region_DoesNotCarryConsentGrantsOrThreshold()
    {
        this.Build().CarryOverRegionFromPreviousSubscription(NewSubscriptionId, TenantId);

        var row = this.savedConsents.Single();
        Assert.IsNull(row.RuntimeAppConsentedUtc);
        Assert.IsNull(row.ConsentedByUpn);
        Assert.IsNull(row.TeamsActivityAppConsentedUtc);
        Assert.IsNull(row.MeteredUserThreshold, "N is captured per subscription at manual activation");
    }

    [TestMethod]
    public void Region_SkipsWhenTheNewSubscriptionAlreadyHasARow()
    {
        this.consentRepo.Setup(x => x.GetByAmpSubscriptionId(NewSubscriptionId))
            .Returns(new SubscriptionTenantConsent { Id = 9, AmpSubscriptionId = NewSubscriptionId });

        var minted = this.Build().CarryOverRegionFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.IsFalse(minted);
        Assert.AreEqual(0, this.savedConsents.Count);
    }

    [TestMethod]
    public void Region_SkipsWhenThePriorSubscriptionIsStillLive()
    {
        this.subscriptionsRepo.Setup(x => x.GetById(PriorSubscriptionId, It.IsAny<bool>()))
            .Returns(new Subscriptions
            {
                AmpsubscriptionId = PriorSubscriptionId,
                SubscriptionStatus = "Subscribed",
                PurchaserTenantId = TenantId,
            });

        var minted = this.Build().CarryOverRegionFromPreviousSubscription(NewSubscriptionId, TenantId);

        Assert.IsFalse(minted);
        Assert.AreEqual(0, this.savedConsents.Count);
    }

    [TestMethod]
    public void Region_SkipsWhenThePredecessorHasNoRegionOrDoesNotExist()
    {
        this.priorConsent.AzureRegion = null;
        Assert.IsFalse(this.Build().CarryOverRegionFromPreviousSubscription(NewSubscriptionId, TenantId));

        this.priorConsent = null;
        Assert.IsFalse(this.Build().CarryOverRegionFromPreviousSubscription(NewSubscriptionId, TenantId));

        Assert.IsFalse(this.Build().CarryOverRegionFromPreviousSubscription(NewSubscriptionId, Guid.Empty), "first purchase");
        Assert.AreEqual(0, this.savedConsents.Count);
    }

    [TestMethod]
    public void Region_SwallowsRepositoryFailuresSoActivationStillCompletes()
    {
        this.consentRepo.Setup(x => x.Save(It.IsAny<SubscriptionTenantConsent>()))
            .Throws(new InvalidOperationException("db down"));

        Assert.IsFalse(this.Build().CarryOverRegionFromPreviousSubscription(NewSubscriptionId, TenantId));
    }

    [TestMethod]
    public void Region_ThenSites_BothCarryOnAResubscribe()
    {
        // The activation-time region carry-over mints the consent row; the first Setup load must
        // still seed the site list afterwards (the old "no consent row" guard would have blocked it).
        var service = this.Build();
        Assert.IsTrue(service.CarryOverRegionFromPreviousSubscription(NewSubscriptionId, TenantId));

        this.consentRepo.Setup(x => x.GetByAmpSubscriptionId(NewSubscriptionId))
            .Returns(() => this.savedConsents.Single());

        Assert.AreEqual(1, service.CarryOverFromPreviousSubscription(NewSubscriptionId, TenantId));
        Assert.AreEqual("Pending", this.saved.Single().Status);
    }
}
