// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Decodes the audit-chain key configured outside the database.
/// </summary>
public static class AuditChainKeyMaterial
{
    /// <summary>Minimum key length, in bytes, accepted for a chain MAC.</summary>
    public const int MinimumLength = 32;

    /// <summary>
    /// Decodes <paramref name="configured"/>. An absent value returns an empty buffer,
    /// which leaves the chain without a key. A present value that is not base64, or that
    /// decodes to fewer than <see cref="MinimumLength"/> bytes, fails closed.
    /// </summary>
    /// <param name="configured">The <c>AuditLog:ChainVerification:Key</c> value, or <c>null</c>.</param>
    /// <returns>The key bytes, or empty when no key is configured.</returns>
    /// <exception cref="InvalidOperationException">The configured value is present but not usable.</exception>
    public static ReadOnlyMemory<byte> Decode(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "AuditLog:ChainVerification:Key must be a base64 value.",
                exception);
        }

        if (bytes.Length < MinimumLength)
        {
            throw new InvalidOperationException(
                "AuditLog:ChainVerification:Key must decode to at least 32 bytes.");
        }

        return bytes;
    }
}
