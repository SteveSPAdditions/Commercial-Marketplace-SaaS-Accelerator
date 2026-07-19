// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using Azure.Identity;
using Marketplace.SaaS.Accelerator.CustomerSite.Controllers;
using Marketplace.SaaS.Accelerator.CustomerSite.Controllers.Api;
using Marketplace.SaaS.Accelerator.CustomerSite.HostedServices;
using Marketplace.SaaS.Accelerator.CustomerSite.WebHook;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Services;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Marketplace.SaaS.Accelerator.Services.WebHook;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Marketplace.SaaS;
using System;
using System.Diagnostics;
using System.Reflection;

namespace Marketplace.SaaS.Accelerator.CustomerSite;

/// <summary>
/// Defines the <see cref="Startup" />.
/// </summary>
public class Startup
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Startup"/> class.
    /// </summary>
    public Startup(IConfiguration configuration, IHostEnvironment hostEnvironment)
    {
        this.Configuration = configuration;
        this.HostEnvironment = hostEnvironment;
    }

    /// <summary>Gets the Configuration.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>Gets the IHostEnvironment, used to resolve content-root-relative paths (e.g. for the SPP signing cert).</summary>
    public IHostEnvironment HostEnvironment { get; }

    /// <summary>
    /// The ConfigureServices.
    /// </summary>
    /// <param name="services">The services<see cref="IServiceCollection"/>.</param>
    public void ConfigureServices(IServiceCollection services)
    {
        // Application Insights. Only register when the connection string is genuinely
        // valid -- AddApplicationInsightsTelemetry() throws ArgumentException at startup
        // if APPLICATIONINSIGHTS_CONNECTION_STRING is present but blank or malformed,
        // which would 500.30 the whole app. Absent setting is fine; empty is fatal.
        var aiConnStr = this.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(aiConnStr)
            && aiConnStr.IndexOf("InstrumentationKey", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            services.AddApplicationInsightsTelemetry();
        }

        services.Configure<CookiePolicyOptions>(options =>
        {
            // This lambda determines whether user consent for non-essential cookies is needed for a given request.
            options.CheckConsentNeeded = context => true;
            options.MinimumSameSitePolicy = SameSiteMode.None;
        });

        var config = new SaaSApiClientConfiguration()
        {
            AdAuthenticationEndPoint = this.Configuration["SaaSApiConfiguration:AdAuthenticationEndPoint"],
            ClientId = this.Configuration["SaaSApiConfiguration:ClientId"],
            ClientSecret = this.Configuration["SaaSApiConfiguration:ClientSecret"],
            MTClientId = this.Configuration["SaaSApiConfiguration:MTClientId"],
            FulFillmentAPIBaseURL = this.Configuration["SaaSApiConfiguration:FulFillmentAPIBaseURL"],
            FulFillmentAPIVersion = this.Configuration["SaaSApiConfiguration:FulFillmentAPIVersion"],
            GrantType = this.Configuration["SaaSApiConfiguration:GrantType"],
            Resource = this.Configuration["SaaSApiConfiguration:Resource"],
            SaaSAppUrl = this.Configuration["SaaSApiConfiguration:SaaSAppUrl"],
            SignedOutRedirectUri = this.Configuration["SaaSApiConfiguration:SignedOutRedirectUri"],
            TenantId = this.Configuration["SaaSApiConfiguration:TenantId"],
            Environment = this.Configuration["SaaSApiConfiguration:Environment"],
            AzRegionSvcUrl = this.Configuration["SaaSApiConfiguration:AzRegionSvcUrl"],
            AzRegionSvcUrlTemplate = this.Configuration["SaaSApiConfiguration:AzRegionSvcUrlTemplate"],
            AzRegionSvcRegions = this.Configuration["SaaSApiConfiguration:AzRegionSvcRegions"],
            LegerisSignalingEndpointUrl = this.Configuration["SaaSApiConfiguration:LegerisSignalingEndpointUrl"],
            LegerisSignalingHmacSecret = this.Configuration["SaaSApiConfiguration:LegerisSignalingHmacSecret"],
            WebhookBufferHmacSecret = this.Configuration["SaaSApiConfiguration:WebhookBufferHmacSecret"],
            AzureRegionSelectorsFallback = this.Configuration["SaaSApiConfiguration:AzureRegionSelectorsFallback"],
            RuntimeAppClientId = this.Configuration["SaaSApiConfiguration:RuntimeAppClientId"],
            TeamsActivityAppClientId = this.Configuration["SaaSApiConfiguration:TeamsActivityAppClientId"],
            MTCertPath = this.Configuration["SaaSApiConfiguration:MTCertPath"],
            MTCertPassword = this.Configuration["SaaSApiConfiguration:MTCertPassword"],
            OutboxMaxAttempts = int.TryParse(this.Configuration["SaaSApiConfiguration:OutboxMaxAttempts"], out var oma) ? oma : 12,
            OutboxDrainIntervalSeconds = int.TryParse(this.Configuration["SaaSApiConfiguration:OutboxDrainIntervalSeconds"], out var odi) ? odi : 30,
            RedirectActivateToSetup = bool.TryParse(this.Configuration["SaaSApiConfiguration:RedirectActivateToSetup"], out var ras) && ras,
        };
        var creds = new ClientSecretCredential(config.TenantId.ToString(), config.ClientId.ToString(), config.ClientSecret);

        // Scopes the portal requests at *initial sign-in* -- authentication only. Keep this
        // minimal: anything listed here is injected into the /authorize request and shown on
        // the portal login consent screen. Sites.FullControl.All is deliberately NOT here --
        // it is requested just-in-time during Setup (Step 4) via [AuthorizeForScopes] on the
        // SetupController Resume actions, so the customer admin only sees that prompt at the
        // point they actually configure sites for Sites.Selected, not at first login.
        var graphScopes = new[]
        {
            "User.Read",
        };

        // Resolve the SPP signing certificate path. Allows the App Setting to be either
        // absolute (e.g. D:\home\site\wwwroot\cert\portal.pfx on App Service) or relative
        // to ContentRoot (e.g. cert/portal.pfx).
        string resolvedMtCertPath = null;
        if (!string.IsNullOrWhiteSpace(config.MTCertPath))
        {
            resolvedMtCertPath = System.IO.Path.IsPathRooted(config.MTCertPath)
                ? config.MTCertPath
                : System.IO.Path.Combine(this.HostEnvironment.ContentRootPath, config.MTCertPath);
        }

        services
            .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(opts =>
            {
                opts.Instance = string.IsNullOrEmpty(config.AdAuthenticationEndPoint)
                    ? "https://login.microsoftonline.com/"
                    : config.AdAuthenticationEndPoint.TrimEnd('/') + "/";
                opts.TenantId = "common"; // multi-tenant: customers from any work/school tenant
                opts.ClientId = config.MTClientId;
                // Use the Microsoft.Identity.Web default (/signin-oidc) rather than colliding
                // with the HomeController's /Home/Index route. The previous raw OIDC handler
                // got away with /Home/Index because the response was a query-string GET; the
                // new flow uses form_post which would otherwise hit MVC and trip antiforgery.

                if (!string.IsNullOrWhiteSpace(resolvedMtCertPath))
                {
                    opts.ClientCertificates = new[]
                    {
                        new Microsoft.Identity.Web.CertificateDescription
                        {
                            SourceType = Microsoft.Identity.Web.CertificateSource.Path,
                            CertificateDiskPath = resolvedMtCertPath,
                            CertificatePassword = config.MTCertPassword ?? string.Empty,
                        },
                    };
                }
            })
            .EnableTokenAcquisitionToCallDownstreamApi(graphScopes)
            .AddInMemoryTokenCaches();

        // Configure the underlying OIDC options that Microsoft.Identity.Web wired up.
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, opts =>
        {
            opts.SignedOutRedirectUri = config.SignedOutRedirectUri;
            opts.TokenValidationParameters.NameClaimType = Marketplace.SaaS.Accelerator.Services.Utilities.ClaimConstants.CLAIM_SHORT_NAME;
            // SaaS Accelerator signs in users from arbitrary customer tenants -> skip the
            // issuer validation that single-tenant apps rely on.
            opts.TokenValidationParameters.ValidateIssuer = false;
        });

        services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, opts =>
        {
            opts.ExpireTimeSpan = TimeSpan.FromMinutes(60);
            opts.Cookie.MaxAge = opts.ExpireTimeSpan;
            opts.SlidingExpiration = true;
        });
        // --- Machine-to-machine bearer auth for the subscription API (React "Upgrade" button) ---
        // A SEPARATE NAMED scheme so it never disturbs the OIDC cookie sign-in the MVC portal
        // uses. Tokens are minted for the Read & Understood runtime app (RuntimeAppClientId) by
        // our own tenant, so - unlike the inbound marketplace webhook token, whose appid must be
        // Microsoft's 20e940b3 app - a first-party backend CAN produce these. Validates audience
        // (== RuntimeAppClientId) + issuer (our tenant) + signature + lifetime.
        var apiAuthorityBase = string.IsNullOrEmpty(config.AdAuthenticationEndPoint)
            ? "https://login.microsoftonline.com/"
            : config.AdAuthenticationEndPoint.TrimEnd('/') + "/";
        services.AddAuthentication()
            .AddJwtBearer(SubscriptionApiController.SubscriptionApiAuthScheme, opts =>
            {
                opts.Authority = $"{apiAuthorityBase}{config.TenantId}/v2.0";
                opts.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudiences = new[]
                    {
                        config.RuntimeAppClientId,
                        $"api://{config.RuntimeAppClientId}",
                    },
                    ValidateIssuer = true,
                    ValidIssuers = new[]
                    {
                        $"https://login.microsoftonline.com/{config.TenantId}/v2.0",
                        $"https://sts.windows.net/{config.TenantId}/",
                    },
                    ValidateLifetime = true,
                };
            });

        services
            .AddTransient<IClaimsTransformation, CustomClaimsTransformation>()
            .AddScoped<ExceptionHandlerAttribute>()
            .AddScoped<RequestLoggerActionFilter>()
            .AddScoped<Marketplace.SaaS.Accelerator.CustomerSite.WebHook.BufferHmacFilter>();

        if (!Uri.TryCreate(config.FulFillmentAPIBaseURL, UriKind.Absolute, out var fulfillmentBaseApi)) 
        {
            fulfillmentBaseApi = new Uri("https://marketplaceapi.microsoft.com/api");
        }

        services
            .AddSingleton<FulfillmentApiClientLogger>()
            .AddSingleton<IFulfillmentApiService>(sp => new FulfillmentApiService(
                new MarketplaceSaaSClient(fulfillmentBaseApi, creds),
                config,
                sp.GetRequiredService<FulfillmentApiClientLogger>()))
            .AddSingleton<SaaSApiClientConfiguration>(config)
            .AddSingleton<ValidateJwtToken>();

        // Add the assembly version
        services.AddSingleton<IAppVersionService>(new AppVersionService(Assembly.GetExecutingAssembly()?.GetName()?.Version));

        services
            .AddDbContext<SaasKitContext>(options => options.UseSqlServer(this.Configuration.GetConnectionString("DefaultConnection")));

        InitializeRepositoryServices(services);

        services.AddMvc(option => {
            option.EnableEndpointRouting = false;
            option.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        });
    }

    /// <summary>
    /// The Configure.
    /// </summary>
    /// <param name="app">The app<see cref="IApplicationBuilder" />.</param>
    /// <param name="env">The env<see cref="IWebHostEnvironment" />.</param>
    /// <param name="loggerFactory">The loggerFactory<see cref="ILoggerFactory" />.</param>
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Home/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseCookiePolicy();
        app.UseAuthentication();
        app.UseMvc(routes =>
        {
            routes.MapRoute(
                name: "default",
                template: "{controller=Home}/{action=Index}/{id?}");
        });
    }

    private static void InitializeRepositoryServices(IServiceCollection services)
    {
        services.AddScoped<ISubscriptionsRepository, SubscriptionsRepository>();
        services.AddScoped<IPlansRepository, PlansRepository>();
        services.AddScoped<IUsersRepository, UsersRepository>();
        services.AddScoped<ISubscriptionLogRepository, SubscriptionLogRepository>();
        services.AddScoped<IApplicationLogRepository, ApplicationLogRepository>();
        services.AddScoped<IWebhookProcessor, WebhookProcessor>();
        services.AddScoped<IWebhookHandler, WebHookHandler>();
        services.AddScoped<IApplicationConfigRepository, ApplicationConfigRepository>();
        services.AddScoped<IEmailTemplateRepository, EmailTemplateRepository>();
        services.AddScoped<IOffersRepository, OffersRepository>();
        services.AddScoped<IOfferAttributesRepository, OfferAttributesRepository>();
        services.AddScoped<IPlanEventsMappingRepository, PlanEventsMappingRepository>();
        services.AddScoped<IEventsRepository, EventsRepository>();
        services.AddScoped<IEmailService, SMTPEmailService>();
        services.AddScoped<SaaSClientLogger<HomeController>>();
        services.AddScoped<IWebNotificationService, WebNotificationService>();
        services.AddScoped<ISubscriptionTenantConsentRepository, SubscriptionTenantConsentRepository>();
        services.AddScoped<ISubscriptionSiteRepository, SubscriptionSiteRepository>();
        services.AddScoped<INotificationOutboxRepository, NotificationOutboxRepository>();
        services.AddScoped<IWebhookOperationLogRepository, WebhookOperationLogRepository>();
        services.AddScoped<SaaSClientLogger<SetupController>>();

        services.AddHttpClient<IAzureRegionService, AzureRegionService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddScoped<ITenantAdminConsentService, TenantAdminConsentService>();
        services.AddScoped<ISetupStatusService, SetupStatusService>();

        services.AddHttpClient<IOutboxDispatcher, LegerisSignalingDispatcher>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<ISitePermissionService, SitePermissionService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHostedService<OutboxDrainService>();
    }
}