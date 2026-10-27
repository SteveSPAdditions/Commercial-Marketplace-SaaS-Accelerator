using System;
using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Marketplace.SaaS.Accelerator.Services.Test
{
    /// <summary>
    /// Smoke tests for this repo's duplicated copy of AppOnlyCredentialFactory (source of truth:
    /// the TFVC repo, Legeris.Office365.ServiceInterface\EntraId\AppOnlyCredentialFactory.cs,
    /// covered there by a full NUnit suite). These pin the contract the three Startup/Program
    /// conversion sites rely on: Certificate default (no config change = client-secret behaviour
    /// unchanged), the SaaSApiConfiguration:ClientId legacy fallback, and per-(tenant, app)
    /// credential caching in Federated mode.
    /// </summary>
    [TestClass]
    public sealed class AppOnlyCredentialFactoryTests
    {
        // ReadSetting consults the process environment before the config delegate, so a stray
        // Auth__* variable on the developer's machine or CI agent would change these outcomes.
        // Clear them for the duration of each test and restore afterwards.
        private static readonly string[] IsolatedEnvironmentVariables =
        {
            "Auth__Mode", "Auth:Mode",
            "Auth__FulfilmentAppId", "Auth:FulfilmentAppId",
            "Auth__ManagedIdentityClientId", "Auth:ManagedIdentityClientId",
            "Auth__DevelopmentCredential", "Auth:DevelopmentCredential",
            "SaaSApiConfiguration:ClientId",
        };

        private readonly Dictionary<string, string> savedEnvironment = new Dictionary<string, string>();

        [TestInitialize]
        public void Initialize()
        {
            foreach (var name in IsolatedEnvironmentVariables)
            {
                savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            AppOnlyCredentialFactory.ReadConfigSetting = null;
            AppOnlyCredentialFactory.ResetForTests();
            foreach (var pair in savedEnvironment)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            savedEnvironment.Clear();
        }

        [TestMethod]
        public void DescribeMode_UsesTheResolvedFulfilmentAppId_WhenPassedByTheHost()
        {
            AppOnlyCredentialFactory.ReadConfigSetting = _ => null;
            AppOnlyCredentialFactory.ResetForTests();

            var line = AppOnlyCredentialFactory.DescribeMode(fulfilmentAppId: "252c2797-0000-0000-0000-0000000000ab");

            StringAssert.Contains(line, "fulfilment=252c");
            StringAssert.Contains(line, "Certificate");
        }

        [TestMethod]
        public void Mode_DefaultsToCertificate_WhenNothingConfigured()
        {
            AppOnlyCredentialFactory.ReadConfigSetting = _ => null;
            AppOnlyCredentialFactory.ResetForTests();

            Assert.AreEqual(AppOnlyAuthMode.Certificate, AppOnlyCredentialFactory.Mode);
        }

        [TestMethod]
        public void ReadSetting_FallsBackToSaaSApiConfigurationClientId_TheAcceleratorLegacyKey()
        {
            AppOnlyCredentialFactory.ReadConfigSetting =
                key => key == "SaaSApiConfiguration:ClientId" ? "252c2797-legacy" : null;
            AppOnlyCredentialFactory.ResetForTests();

            Assert.AreEqual("252c2797-legacy",
                AppOnlyCredentialFactory.ReadSetting("FulfilmentAppId", new[] { "SaaSApiConfiguration:ClientId" }));
        }

        [TestMethod]
        public void GetFederatedCredential_CachesPerTenantAndApp()
        {
            AppOnlyCredentialFactory.ReadConfigSetting =
                key => key == "Auth:Mode" ? "Federated" : null;
            AppOnlyCredentialFactory.ResetForTests();

            var first = AppOnlyCredentialFactory.GetFederatedCredential("tenant-a", "app-1");
            var second = AppOnlyCredentialFactory.GetFederatedCredential("tenant-a", "app-1");
            var other = AppOnlyCredentialFactory.GetFederatedCredential("tenant-a", "app-2");

            Assert.IsTrue(ReferenceEquals(first, second));
            Assert.IsFalse(ReferenceEquals(first, other));
        }
    }
}
