// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System.Collections.Generic;

namespace Marketplace.SaaS.Accelerator.Services.Models;

/// <summary>
/// Response shape from the Read and Understood AzRSvc Function1 service.
/// Mirrors the existing TenantRegionReqResponse in the Legeris codebase.
/// </summary>
public class TenantRegionInfo
{
    /// <summary>The tenant's existing Azure region, or "?" if unknown.</summary>
    public string AzRegion { get; set; }

    /// <summary>Publisher-maintained list of selectable regions.</summary>
    public List<RegionSelector> AzureRegionSelectors { get; set; } = new();

    /// <summary>Non-null when Function1 returned an error or fallback was used.</summary>
    public string Error { get; set; }

    /// <summary>True when this result came from the configured fallback list, not Function1.</summary>
    public bool IsFallback { get; set; }
}

/// <summary>A single region selector entry: {key, text}.</summary>
public class RegionSelector
{
    public string Key { get; set; }
    public string Text { get; set; }
}
