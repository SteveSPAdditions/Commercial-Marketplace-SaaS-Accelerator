using System;
using System.Diagnostics;
using System.Reflection;
using Azure.Core;
using Azure.Identity;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Services;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Marketplace.Metering;

namespace Marketplace.SaaS.Accelerator.MeteredTriggerJob;

class Program
{
    /// <summary>
    /// Entery point to the scheduler engine
    /// </summary>
    /// <param name="args"></param>
    static void Main (string[] args)
    {

        Console.WriteLine($"MeteredExecutor Webjob Started at: {DateTime.Now}");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var config = new SaaSApiClientConfiguration()
        {
            AdAuthenticationEndPoint = configuration["SaaSApiConfiguration:AdAuthenticationEndPoint"],
            ClientId = configuration["SaaSApiConfiguration:ClientId"],
            ClientSecret = configuration["SaaSApiConfiguration:ClientSecret"],
            GrantType = configuration["SaaSApiConfiguration:GrantType"],
            Resource = configuration["SaaSApiConfiguration:Resource"],
            TenantId = configuration["SaaSApiConfiguration:TenantId"]
        };

        // App-only auth mode: Auth:Mode=Federated exchanges the host's user-assigned managed
        // identity for a fulfilment-app token (no secret); default (Certificate) is the
        // pre-existing client secret, byte-for-byte. This job runs inside the admin App Service,
        // so it sees the same application settings / identity.
        AppOnlyCredentialFactory.WireUp(
            key => configuration[key],
            (message, exception) => Console.WriteLine($"WARN {message} {exception}"));
        AppOnlyCredentialFactory.AssertStartupPreconditions();
        var fulfilmentAppId =
            AppOnlyCredentialFactory.ReadSetting("FulfilmentAppId", new[] { "SaaSApiConfiguration:ClientId" })
            ?? config.ClientId.ToString();
        Console.WriteLine(AppOnlyCredentialFactory.DescribeMode(fulfilmentAppId: fulfilmentAppId));
        TokenCredential creds = AppOnlyCredentialFactory.Mode == AppOnlyAuthMode.Federated
            ? AppOnlyCredentialFactory.GetFederatedCredential(config.TenantId.ToString(), fulfilmentAppId)
            : new ClientSecretCredential(config.TenantId.ToString(), config.ClientId.ToString(), config.ClientSecret);
        var versionInfo = new AppVersionService(Assembly.GetExecutingAssembly()?.GetName()?.Version);

        var services = new ServiceCollection()
            .AddDbContext<SaasKitContext>(options => options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")), ServiceLifetime.Transient)
            .AddScoped<ISchedulerFrequencyRepository, SchedulerFrequencyRepository>()
            .AddScoped<IMeteredPlanSchedulerManagementRepository, MeteredPlanSchedulerManagementRepository>()
            .AddScoped<ISchedulerManagerViewRepository, SchedulerManagerViewRepository>()
            .AddScoped<ISubscriptionUsageLogsRepository, SubscriptionUsageLogsRepository>()
            .AddScoped<IApplicationLogRepository, ApplicationLogRepository>()
            .AddScoped<IEmailService, SMTPEmailService>()
            .AddScoped<IEmailTemplateRepository, EmailTemplateRepository>()
            .AddScoped<IApplicationConfigRepository, ApplicationConfigRepository>()
            .AddSingleton<IMeteredBillingApiService>(new MeteredBillingApiService(new MarketplaceMeteringClient(creds), config, new SaaSClientLogger<MeteredBillingApiService>()))
            .AddSingleton<Executor, Executor>()
            .AddSingleton<IAppVersionService>(versionInfo)
            .BuildServiceProvider();

        services
            .GetService<Executor>()
            .Execute();
        Console.WriteLine($"MeteredExecutor Webjob Ended at: {DateTime.Now}");

    }
}