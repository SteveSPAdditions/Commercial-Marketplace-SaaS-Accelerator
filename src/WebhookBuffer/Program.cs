// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Net.Http.Headers;
using Azure.Messaging.ServiceBus;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Options;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        services
            .AddOptions<BufferOptions>()
            .Bind(context.Configuration.GetSection(BufferOptions.SectionName));

        services
            .AddOptions<PortalOptions>()
            .Bind(context.Configuration.GetSection(PortalOptions.SectionName))
            .PostConfigure(o => o.Validate());

        services
            .AddOptions<AadOptions>()
            .Bind(context.Configuration.GetSection(AadOptions.SectionName))
            .PostConfigure(o => o.Validate());

        services.AddSingleton<IJwtValidator, JwtValidator>();

        services.AddHttpClient<IPortalClient, PortalClient>((sp, client) =>
        {
            var portal = sp.GetRequiredService<IOptions<PortalOptions>>().Value;
            client.BaseAddress = new Uri(portal.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(portal.TimeoutSeconds);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddSingleton(sp =>
        {
            var connection = context.Configuration.GetValue<string>("ServiceBusConnection")
                ?? throw new InvalidOperationException("ServiceBusConnection setting is required.");
            return new ServiceBusClient(connection);
        });

        services.AddSingleton(sp =>
        {
            var sbClient = sp.GetRequiredService<ServiceBusClient>();
            var bufferOptions = sp.GetRequiredService<IOptions<BufferOptions>>().Value;
            return sbClient.CreateSender(bufferOptions.QueueName);
        });

        services.AddApplicationInsightsTelemetryWorkerService();
    })
    .Build();

host.Run();
