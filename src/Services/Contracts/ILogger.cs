// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Logger Interface
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Debugs the specified message.
    /// </summary>
    /// <param name="message">The message.</param>
    void Debug(string message);

    /// <summary>
    /// Debugs the specified message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ex">The ex.</param>
    void Debug(string message, Exception ex);

    /// <summary>
    /// Information the specified message.
    /// </summary>
    /// <param name="message">The message.</param>
    void Info(string message);

    /// <summary>
    /// Information the specified message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ex">The ex.</param>
    void Info(string message, Exception ex);

    /// <summary>
    /// Warns the specified message.
    /// </summary>
    /// <param name="message">The message.</param>
    void Warn(string message);

    /// <summary>
    /// Warns the specified message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ex">The ex.</param>
    void Warn(string message, Exception ex);

    /// <summary>
    /// Errors the specified message.
    /// </summary>
    /// <param name="message">The message.</param>
    void Error(string message);

    /// <summary>
    /// Errors the specified message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ex">The ex.</param>
    void Error(string message, Exception ex);

    /// <summary>
    /// Logs a failed outbound Microsoft Marketplace API call with structured, queryable
    /// properties (Stage, MarketplaceAction, StatusCode). Implementations forward these as
    /// message-template properties so the failure surfaces as Application Insights
    /// customDimensions and can be attributed to the Marketplace API stage — distinct from
    /// the in-project auth/processing stages.
    /// </summary>
    /// <param name="marketplaceAction">The Marketplace action being attempted (e.g. CHANGE_PLAN, ACTIVATE, SUBSCRIPTION_USAGEEVENT).</param>
    /// <param name="statusCode">The HTTP status code returned by Microsoft, or 0 when unknown.</param>
    /// <param name="ex">The exception raised by the client library.</param>
    void MarketplaceApiError(string marketplaceAction, int statusCode, Exception ex);
}