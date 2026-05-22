// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Marketplace.SaaS.Accelerator.WebhookBuffer.Test;

[TestClass]
public class HmacSignerTests
{
    private const string Secret = "ZmFrZS1zZWNyZXQtZm9yLXVuaXQtdGVzdHM=";

    [TestMethod]
    public void ComputeSignature_IsDeterministic()
    {
        const string body = "{\"id\":\"op-1\"}";
        var first = HmacSigner.ComputeSignature(body, Secret);
        var second = HmacSigner.ComputeSignature(body, Secret);
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void ComputeSignature_IsLowercaseHex64Chars()
    {
        var sig = HmacSigner.ComputeSignature("body", Secret);
        Assert.AreEqual(64, sig.Length);
        Assert.AreEqual(sig, sig.ToLowerInvariant());
    }

    [TestMethod]
    public void ComputeSignature_ChangesWhenBodyChanges()
    {
        var a = HmacSigner.ComputeSignature("{\"a\":1}", Secret);
        var b = HmacSigner.ComputeSignature("{\"a\":2}", Secret);
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void Verify_AcceptsMatchingSignature()
    {
        const string body = "{\"hello\":\"world\"}";
        var sig = HmacSigner.ComputeSignature(body, Secret);
        Assert.IsTrue(HmacSigner.Verify(body, Secret, sig));
    }

    [TestMethod]
    public void Verify_RejectsMismatchedSignature()
    {
        Assert.IsFalse(HmacSigner.Verify("body", Secret, "0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [TestMethod]
    public void Verify_RejectsEmptySignature()
    {
        Assert.IsFalse(HmacSigner.Verify("body", Secret, string.Empty));
    }
}
