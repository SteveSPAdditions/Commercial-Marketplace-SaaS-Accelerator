// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Security.Cryptography;
using System.Text;

namespace Marketplace.SaaS.Accelerator.Services.Utilities;

/// <summary>
/// Canonical HMAC-SHA256 body signer used by both directions of the Legeris signaling
/// path and the inbound webhook buffer. Key material is the UTF-8 bytes of the configured
/// secret string (the secret is typically base64-encoded random bytes but is treated as
/// an opaque UTF-8 string here — both sides must agree on the byte representation).
/// </summary>
public static class HmacSigner
{
    /// <summary>
    /// Computes lowercase-hex HMAC-SHA256 of <paramref name="body"/> using
    /// <paramref name="secret"/> as the key.
    /// </summary>
    public static string ComputeSignature(string body, string secret)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
        var keyBytes = Encoding.UTF8.GetBytes(secret ?? string.Empty);
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToHexString(hmac.ComputeHash(bodyBytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Constant-time comparison of two signatures. Returns false if either is null/empty
    /// or if lengths differ.
    /// </summary>
    public static bool Verify(string body, string secret, string presentedSignature)
    {
        if (string.IsNullOrEmpty(presentedSignature))
        {
            return false;
        }

        var expected = ComputeSignature(body, secret);
        if (expected.Length != presentedSignature.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(presentedSignature));
    }
}
