// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using Marketplace.SaaS.Accelerator.Services.Models;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// The terms-acceptance gate in front of the Setup wizard: the customer must confirm they agree to
/// the Microsoft Standard Contract and the publisher's amendment before any Setup step can be
/// actioned. Documents and the kill switch live in ApplicationConfiguration; acceptances are
/// appended to SubscriptionTermsAcceptance.
/// </summary>
public interface ITermsAcceptanceService
{
    /// <summary>The two configured documents and the kill switch, independent of any subscription.</summary>
    TermsAcceptanceStatus GetConfiguration();

    /// <summary>Evaluate the gate for one subscription.</summary>
    TermsAcceptanceStatus GetStatus(Guid ampSubscriptionId);

    /// <summary>
    /// Persist an acceptance for the subscription: validates both boxes are ticked and the documents
    /// are configured, appends the acceptance row, writes a subscription audit-log entry, and sends
    /// the confirmation email (best effort -- an email failure never fails the acceptance).
    /// </summary>
    TermsAcceptanceResult Record(Guid ampSubscriptionId, TermsAcceptanceRequest request);
}
