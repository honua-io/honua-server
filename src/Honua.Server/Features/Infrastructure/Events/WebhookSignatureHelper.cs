// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Shared HMAC-SHA256 signature computation for outbound webhook delivery.
/// </summary>
internal static class WebhookSignatureHelper
{
    /// <summary>
    /// Computes a hex-encoded HMAC-SHA256 signature over <c>{timestamp}.{payload}</c>.
    /// </summary>
    public static string ComputeSignature(string secret, string timestamp, string payload)
    {
        var message = $"{timestamp}.{payload}";
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(messageBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
