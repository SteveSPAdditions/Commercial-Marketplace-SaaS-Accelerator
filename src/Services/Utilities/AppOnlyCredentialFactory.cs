//
// DUPLICATED from the TFVC repo: Legeris.Office365.ServiceInterface\EntraId\AppOnlyCredentialFactory.cs
// (this fork is a separate git repo and cannot link across repos) -- keep in sync manually.
// Only the namespace differs. Must compile against Azure.Identity 1.11.4 (this repo's version).
//
using Azure.Core;
using Azure.Identity;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable once CheckNamespace
namespace Marketplace.SaaS.Accelerator.Services.Utilities
{
    public enum AppOnlyAuthMode
    {
        /// <summary>Legacy credential: the per-region certificate, or a client secret on the
        /// fulfilment legs. Default when Auth:Mode is absent -- the pre-existing code paths run
        /// byte-for-byte unchanged.</summary>
        Certificate,

        /// <summary>The host's managed identity, presented to Entra as a federated credential
        /// (client assertion) on the target app registration.</summary>
        Federated,
    }

    /// <summary>
    /// Single switch point for app-only authentication. In <see cref="AppOnlyAuthMode.Federated"/>
    /// mode it exchanges the host's managed-identity token (scope
    /// <see cref="TokenExchangeScope"/>) for tokens of the configured app registrations; in
    /// <see cref="AppOnlyAuthMode.Certificate"/> mode callers never come here -- each call site
    /// keeps its existing certificate/secret expression in the other branch.
    ///
    /// Configuration (read through <see cref="ReadSetting"/>, one precedence ladder for every host):
    ///   Auth:Mode                     Certificate (default) | Federated
    ///   Auth:ManagedIdentityClientId  client id of the user-assigned identity; ABSENT on hosts
    ///                                 that use their system-assigned identity (metering)
    ///   Auth:ProductAppId / Auth:TeamsAppId / Auth:FulfilmentAppId
    ///                                 which app registration to authenticate AS (used by the
    ///                                 startup line and by call sites via their legacy fallbacks)
    ///   Auth:DevelopmentCredential    "true" to allow DefaultAzureCredential (az login) instead
    ///                                 of a managed identity -- honoured in DEBUG builds ONLY
    ///
    /// Precedence per key K: environment variable Auth__K, then Auth:K; then the host delegate
    /// (<see cref="ReadConfigSetting"/>) with Auth:K, then Auth__K; then any legacy keys verbatim
    /// through the same ladder; then the supplied default. Environment-first is load-bearing: the
    /// Events WebJob reads the deployed physical web.config, so App Service application settings
    /// reach it only as environment variables.
    /// </summary>
    public static class AppOnlyCredentialFactory
    {
        /// <summary>The fixed token-exchange audience for managed-identity federation.</summary>
        public const string TokenExchangeScope = "api://AzureADTokenExchange/.default";

        private const string SettingPrefixColon = "Auth:";
        private const string SettingPrefixUnderscore = "Auth__";

        /// <summary>Host config reader: key -> value (or null). Wired once per host at startup.</summary>
        public static Func<string, string> ReadConfigSetting;

        /// <summary>Host log sink: (message, exception-or-null). Warning severity. Wired per host
        /// because AzRSvc's Loging is a no-op stub and the WebJob/web logger differ.</summary>
        public static Action<string, Exception> Log;

        private static AppOnlyAuthMode? _cachedMode;
        private static TokenCredential _exchangeCredential;
        private static readonly object SyncRoot = new object();
        private static readonly ConcurrentDictionary<string, TokenCredential> FederatedCredentials
            = new ConcurrentDictionary<string, TokenCredential>(StringComparer.OrdinalIgnoreCase);

        public static void WireUp(Func<string, string> readConfigSetting, Action<string, Exception> log)
        {
            ReadConfigSetting = readConfigSetting;
            Log = log;
        }

        /// <summary>
        /// Reads one Auth:* setting through the full precedence ladder. <paramref name="shortKey"/>
        /// is the bare name ("Mode", "FulfilmentAppId"); <paramref name="legacyFallbackKeys"/> are
        /// pre-existing keys tried verbatim (e.g. "MarketplaceFulfillmentAppId") so deployments
        /// without the new keys keep working.
        /// </summary>
        public static string ReadSetting(string shortKey, string[] legacyFallbackKeys = null, string defaultValue = null)
        {
            var value = FirstNonEmpty(
                Environment.GetEnvironmentVariable(SettingPrefixUnderscore + shortKey),
                Environment.GetEnvironmentVariable(SettingPrefixColon + shortKey),
                ReadConfigSetting?.Invoke(SettingPrefixColon + shortKey),
                ReadConfigSetting?.Invoke(SettingPrefixUnderscore + shortKey));
            if (value != null) return value;

            if (legacyFallbackKeys != null)
            {
                foreach (var legacyKey in legacyFallbackKeys)
                {
                    value = FirstNonEmpty(
                        Environment.GetEnvironmentVariable(legacyKey),
                        ReadConfigSetting?.Invoke(legacyKey));
                    if (value != null) return value;
                }
            }

            return defaultValue;
        }

        /// <summary>Resolved once and cached (config cannot change without a host restart).
        /// Absent, empty or unrecognised values mean <see cref="AppOnlyAuthMode.Certificate"/>.</summary>
        public static AppOnlyAuthMode Mode
        {
            get
            {
                var cached = _cachedMode;
                if (cached.HasValue) return cached.Value;

                var raw = ReadSetting("Mode");
                var mode = string.Equals(raw, "Federated", StringComparison.OrdinalIgnoreCase)
                    ? AppOnlyAuthMode.Federated
                    : AppOnlyAuthMode.Certificate;
                _cachedMode = mode;
                return mode;
            }
        }

        /// <summary>Client id of the user-assigned managed identity; null/empty means the host's
        /// system-assigned identity (the metering apps).</summary>
        public static string ManagedIdentityClientId => ReadSetting("ManagedIdentityClientId");

        /// <summary>
        /// Call once per host at startup, straight after <see cref="WireUp"/>. In Federated mode
        /// the most likely misconfiguration is a host without a managed-identity endpoint; fail
        /// here with a clear message rather than a 401 deep inside a Graph call.
        /// </summary>
        public static void AssertStartupPreconditions(bool requireUserAssignedIdentity = true)
        {
            if (Mode != AppOnlyAuthMode.Federated) return;

            // The DEBUG DefaultAzureCredential escape hatch never uses the user-assigned id, so a
            // local Federated run must not be forced to invent one.
            var usingDevelopmentCredential = !HasManagedIdentityEndpoint() && DevelopmentCredentialAllowed();

            if (requireUserAssignedIdentity && !usingDevelopmentCredential
                && string.IsNullOrWhiteSpace(ManagedIdentityClientId))
                throw new InvalidOperationException(
                    "Auth:Mode is Federated but Auth:ManagedIdentityClientId is not set. " +
                    "This host authenticates with a user-assigned managed identity; set the identity's " +
                    "client id, or revert Auth:Mode to Certificate.");

            if (!HasManagedIdentityEndpoint() && !DevelopmentCredentialAllowed())
                throw new InvalidOperationException(
                    "Auth:Mode is Federated but the managed-identity endpoint is not available " +
                    $"(IDENTITY_ENDPOINT present: {EnvPresent("IDENTITY_ENDPOINT")}, " +
                    $"IDENTITY_HEADER present: {EnvPresent("IDENTITY_HEADER")}). " +
                    "Either this host has no managed identity assigned, or Federated mode was enabled " +
                    "somewhere it cannot work (e.g. a local run). Revert Auth:Mode to Certificate, or fix " +
                    "the identity assignment.");
        }

        /// <summary>
        /// The one startup line each host logs at Information level. Never includes secrets,
        /// tokens, cert paths or passwords -- ids are truncated, the certificate is identified by
        /// FILE NAME only (this codebase has no thumbprints). On legs whose legacy credential is a
        /// client secret, pass null and the line reads "(legacy client secret)". Hosts whose
        /// fulfilment app id resolves through a legacy key pass the resolved value as
        /// <paramref name="fulfilmentAppId"/> so the line shows what the call sites actually use.
        /// </summary>
        public static string DescribeMode(string certFileName = null, string fulfilmentAppId = null)
        {
            var appIds = "";
            var product = ReadSetting("ProductAppId");
            var teams = ReadSetting("TeamsAppId");
            var fulfilment = string.IsNullOrWhiteSpace(fulfilmentAppId) ? ReadSetting("FulfilmentAppId") : fulfilmentAppId;
            if (product != null) appIds += ", product=" + Shorten(product);
            if (teams != null) appIds += ", teams=" + Shorten(teams);
            if (fulfilment != null) appIds += ", fulfilment=" + Shorten(fulfilment);

            if (Mode == AppOnlyAuthMode.Federated)
            {
                var identity = string.IsNullOrWhiteSpace(ManagedIdentityClientId)
                    ? "system-assigned"
                    : "identity " + Shorten(ManagedIdentityClientId);
                return $"Auth mode: Federated ({identity}{appIds})";
            }

            var credential = string.IsNullOrWhiteSpace(certFileName)
                ? "legacy client secret"
                : "certificate " + certFileName;
            return $"Auth mode: Certificate ({credential}{appIds})";
        }

        /// <summary>
        /// The Federated-mode credential for one (tenant, app) pair -- the tenant the token is
        /// acquired IN (customer tenant for Graph/SPO, publisher tenant for marketplace APIs), the
        /// app registration authenticated AS. Instances are cached per pair: ClientAssertionCredential
        /// caches tokens internally (per scope), but only when the same instance is reused.
        /// </summary>
        public static TokenCredential GetFederatedCredential(string tenantId, string appId)
        {
            if (Mode != AppOnlyAuthMode.Federated)
                throw new InvalidOperationException(
                    "GetFederatedCredential called while Auth:Mode is Certificate. Call sites must " +
                    "keep their existing certificate/secret path in the non-Federated branch.");
            if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentNullException(nameof(appId));

            return FederatedCredentials.GetOrAdd(tenantId + "|" + appId, _ =>
                new ClientAssertionCredential(tenantId, appId,
                    (Func<CancellationToken, Task<string>>)(ct => GetFederationAssertionAsync(ct))));
        }

        /// <summary>
        /// Mints the managed-identity assertion that Federated mode presents to Entra. Also the
        /// plug-in point for MSAL call sites:
        /// <c>.WithClientAssertion((AssertionRequestOptions o) => GetFederationAssertionAsync(o.CancellationToken))</c>.
        /// One process-wide exchange credential is reused so its internal token cache works.
        /// </summary>
        public static async Task<string> GetFederationAssertionAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var credential = GetExchangeCredential();
            var token = await credential
                .GetTokenAsync(new TokenRequestContext(new[] { TokenExchangeScope }), cancellationToken)
                .ConfigureAwait(false);
            return token.Token;
        }

        /// <summary>Clears cached mode and credentials so unit tests can vary configuration.</summary>
        public static void ResetForTests()
        {
            lock (SyncRoot)
            {
                _cachedMode = null;
                _exchangeCredential = null;
                FederatedCredentials.Clear();
            }
        }

        private static TokenCredential GetExchangeCredential()
        {
            var existing = _exchangeCredential;
            if (existing != null) return existing;

            lock (SyncRoot)
            {
                if (_exchangeCredential != null) return _exchangeCredential;

                if (!HasManagedIdentityEndpoint() && DevelopmentCredentialAllowed())
                {
#if DEBUG
                    // Local development only: az login via DefaultAzureCredential, opted into with
                    // Auth:DevelopmentCredential=true. Never compiled into Release builds -- the
                    // chain it probes makes production failures hard to diagnose.
                    Log?.Invoke("AppOnlyCredentialFactory: no managed-identity endpoint; using " +
                                "DefaultAzureCredential (Auth:DevelopmentCredential, DEBUG build only).", null);
                    _exchangeCredential = new DefaultAzureCredential();
                    return _exchangeCredential;
#endif
                }

                // The shared file must build against Azure.Identity 1.11.4 (Accelerator), which
                // predates the ManagedIdentityId API that replaces this constructor.
#pragma warning disable CS0618
                var clientId = ManagedIdentityClientId;
                _exchangeCredential = string.IsNullOrWhiteSpace(clientId)
                    ? new ManagedIdentityCredential()
                    : new ManagedIdentityCredential(clientId);
#pragma warning restore CS0618
                return _exchangeCredential;
            }
        }

        private static bool DevelopmentCredentialAllowed()
        {
#if DEBUG
            return string.Equals(ReadSetting("DevelopmentCredential"), "true", StringComparison.OrdinalIgnoreCase);
#else
            return false;
#endif
        }

        private static bool HasManagedIdentityEndpoint()
            => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"))
               && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_HEADER"));

        private static string EnvPresent(string name)
            => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)) ? "no" : "yes";

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            return null;
        }

        private static string Shorten(string id)
            => string.IsNullOrEmpty(id) || id.Length <= 8
                ? id
                : id.Substring(0, 4) + "…" + id.Substring(id.Length - 2);
    }
}
