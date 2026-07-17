// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Web;
using Marketplace.SaaS.Accelerator.Services.Services;
using Microsoft.Extensions.Logging;
using ILogger = Marketplace.SaaS.Accelerator.Services.Contracts.ILogger;

namespace Marketplace.SaaS.Accelerator.Services.Utilities;

/// <summary>
/// Logger.
/// </summary>
/// <seealso cref="Microsoft.Marketplace.SaaS.SDK.Services.ILogger" />
public class SaaSClientLogger<T> : ILogger
{
    /// <summary>
    /// The logger.
    /// </summary>
    private  ILogger<T> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaaSClientLogger{T}"/> class using a
    /// DI-provided logger, so entries flow through the host's logging pipeline (Application
    /// Insights, filesystem, console -- whatever the host configures). Prefer this in the
    /// web hosts; it is what makes Marketplace API telemetry reach AI. Mirrors the fix
    /// already applied to <see cref="FulfillmentApiClientLogger"/>.
    /// </summary>
    /// <param name="logger">The DI-provided logger.</param>
    public SaaSClientLogger(ILogger<T> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SaaSClientLogger{T}"/> class with a
    /// console-only sink. Fallback for hosts that register no logging pipeline (e.g. the
    /// MeteredTriggerJob console app); telemetry then goes to the WebJob log stream only,
    /// not Application Insights.
    /// </summary>
    public SaaSClientLogger()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddConsole();
        });

        this.logger = loggerFactory.CreateLogger<T>();
    }
    /// <summary>
    /// Log the message at Debug severity.
    /// </summary>
    /// <param name="message">The message.</param>
    public void Debug(string message)
    {
        this.logger.LogDebug(message);
    }

    /// <summary>
    /// Log the message at Debug severity.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ex">The ex.</param>
    public void Debug(string message, Exception ex)
    {
        this.logger.LogDebug(ex, message);
    }

    /// <summary>
    /// Log the message at Error severity.
    /// </summary>
    /// <param name="message">The message.</param>
    public void Error(string message)
    {
        this.logger.LogError(message);
    }

    /// <summary>
    /// Log the message at Error severity.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ex">The ex.</param>
    public void Error(string message, Exception ex)
    {
        this.logger.LogError(ex, message);
    }

    /// <summary>
    /// Log the message at Info severity.
    /// </summary>
    /// <param name="message">The message.</param>
    public void Info(string message)
    {
        this.logger.LogInformation(message);
    }

    /// <summary>
    /// Log the message at Info severity.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ex">The ex.</param>
    public void Info(string message, Exception ex)
    {
        this.logger.LogInformation(ex, message);
    }

    /// <summary>
    /// Log the message at Warn severity.
    /// </summary>
    /// <param name="message">The message.</param>
    public void Warn(string message)
    {
        this.logger.LogWarning(message);
    }

    /// <summary>
    /// Log the message at Warn severity.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ex">The ex.</param>
    public void Warn(string message, Exception ex)
    {
        this.logger.LogWarning(ex, message);
    }


    public void LogError(string message)
    {
        this.logger.LogError(message);
    }

    /// <inheritdoc />
    public void MarketplaceApiError(string marketplaceAction, int statusCode, Exception ex)
    {
        this.logger.LogError(
            ex,
            "Outbound Marketplace API call failed. Stage={Stage} MarketplaceAction={MarketplaceAction} StatusCode={StatusCode}",
            "MarketplaceApi", marketplaceAction, statusCode);
    }

}