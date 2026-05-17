// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.Services.Models;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Computes Setup-flow completion state for one or more subscriptions.
/// Used by both the Setup page (to render the rich step UI) and the
/// Subscriptions list (to render a status pill).
/// </summary>
public interface ISetupStatusService
{
    SetupStatusSummary GetStatus(Guid ampSubscriptionId);

    IDictionary<Guid, SetupStatusSummary> GetStatuses(IEnumerable<Guid> ampSubscriptionIds);
}
