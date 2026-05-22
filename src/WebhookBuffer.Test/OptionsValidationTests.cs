// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using Marketplace.SaaS.Accelerator.WebhookBuffer.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Test;

[TestClass]
public class OptionsValidationTests
{
    [TestMethod]
    public void PortalOptions_Validate_ThrowsWhenBaseUrlMissing()
    {
        var options = new PortalOptions { HmacSecret = "secret" };
        Assert.ThrowsException<InvalidOperationException>(() => options.Validate());
    }

    [TestMethod]
    public void PortalOptions_Validate_ThrowsWhenHmacSecretMissing()
    {
        var options = new PortalOptions { BaseUrl = "https://portal.example" };
        Assert.ThrowsException<InvalidOperationException>(() => options.Validate());
    }

    [TestMethod]
    public void PortalOptions_Validate_PassesWhenBothPresent()
    {
        var options = new PortalOptions { BaseUrl = "https://portal.example", HmacSecret = "secret" };
        options.Validate();
    }

    [TestMethod]
    public void AadOptions_Validate_ThrowsWhenTenantIdMissing()
    {
        var options = new AadOptions
        {
            ClientId = "252c2797-3892-49c2-a5f5-43d2ab5f3538",
            Resource = "20e940b3-4c77-4b0b-9a53-9e16a1b010a7",
        };
        Assert.ThrowsException<InvalidOperationException>(() => options.Validate());
    }

    [TestMethod]
    public void AadOptions_Validate_ThrowsWhenClientIdMissing()
    {
        var options = new AadOptions
        {
            TenantId = Guid.NewGuid().ToString(),
            Resource = "20e940b3-4c77-4b0b-9a53-9e16a1b010a7",
        };
        Assert.ThrowsException<InvalidOperationException>(() => options.Validate());
    }

    [TestMethod]
    public void AadOptions_Validate_ThrowsWhenResourceMissing()
    {
        var options = new AadOptions
        {
            TenantId = Guid.NewGuid().ToString(),
            ClientId = "252c2797-3892-49c2-a5f5-43d2ab5f3538",
        };
        Assert.ThrowsException<InvalidOperationException>(() => options.Validate());
    }

    [TestMethod]
    public void AadOptions_Validate_PassesWhenAllPresent()
    {
        var options = new AadOptions
        {
            TenantId = Guid.NewGuid().ToString(),
            ClientId = "252c2797-3892-49c2-a5f5-43d2ab5f3538",
            Resource = "20e940b3-4c77-4b0b-9a53-9e16a1b010a7",
        };
        options.Validate();
    }
}
