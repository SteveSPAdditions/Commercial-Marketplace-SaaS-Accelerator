// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using Marketplace.SaaS.Accelerator.Services.Models;

namespace Marketplace.SaaS.Accelerator.CustomerSite.Models;

public enum StepState
{
    Locked,
    NotStarted,
    InProgress,
    Complete,
    Failed,
}

public class SetupViewModel
{
    public Guid AmpSubscriptionId { get; set; }
    public string SubscriptionName { get; set; }
    public string PlanId { get; set; }
    public Guid TenantId { get; set; }

    public StepState Step1 { get; set; } = StepState.Complete;
    public StepState Step2 { get; set; }
    public StepState Step3 { get; set; }
    public StepState Step4 { get; set; }

    public RegionPickerViewModel RegionPicker { get; set; }
    public ConsentStepViewModel Consent { get; set; }
    public IReadOnlyList<SiteRowViewModel> Sites { get; set; } = new List<SiteRowViewModel>();

    public string FlashMessage { get; set; }
    public bool FlashIsError { get; set; }
}

public class RegionPickerViewModel
{
    /// <summary>"detected" | "picker" | "fallback" | "saved"</summary>
    public string Mode { get; set; }
    public string SelectedRegion { get; set; }
    public string SelectedRegionFriendly { get; set; }
    public List<RegionSelector> Selectors { get; set; } = new();
    public bool FanOutComplete { get; set; }
    public DateTime? SelectedUtc { get; set; }
    public string SelectedByUpn { get; set; }
    public string ErrorMessage { get; set; }
}

public class ConsentStepViewModel
{
    public bool Granted { get; set; }
    public DateTime? GrantedUtc { get; set; }
    public string GrantedByUpn { get; set; }
}

public class SiteRowViewModel
{
    public int Id { get; set; }
    public string SharePointSiteUrl { get; set; }
    public string Status { get; set; }
    public string CurrentRole { get; set; }
    public DateTime? GrantedUtc { get; set; }
    public string FailureReason { get; set; }
    public bool CanDowngrade { get; set; }
    public bool CanReelevate { get; set; }
}
