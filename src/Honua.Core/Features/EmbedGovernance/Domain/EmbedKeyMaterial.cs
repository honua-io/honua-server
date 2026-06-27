// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.EmbedGovernance.Domain;

/// <summary>
/// Generation, hashing, and recognition of embed API key material. Centralized
/// so the issuing store and analytics validation agree on the key shape.
/// </summary>
public static class EmbedKeyMaterial
{
    /// <summary>Stable, recognizable prefix on every embed key.</summary>
    public const string Prefix = "embk_";

    private const int KeyByteCount = 32;
    private const int DisplayPrefixLength = 14;

    /// <summary>
    /// Generates fresh, URL-safe plaintext key material.
    /// </summary>
    /// <returns>The plaintext key, including the recognizable prefix.</returns>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeyByteCount);
        return Prefix + Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Produces the non-secret display prefix for a plaintext key.
    /// </summary>
    /// <param name="keyMaterial">The plaintext key.</param>
    /// <returns>A short prefix safe to store and show operators.</returns>
    public static string DisplayPrefix(string keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        var length = Math.Min(DisplayPrefixLength, keyMaterial.Length);
        return keyMaterial[..length];
    }

    /// <summary>
    /// Computes the SHA-256 hash of plaintext key material.
    /// </summary>
    /// <param name="keyMaterial">The plaintext key.</param>
    /// <returns>The hash bytes.</returns>
    public static byte[] Hash(string keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        return SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
    }

    /// <summary>
    /// Determines whether a free-form value looks like raw embed key material.
    /// Used to reject analytics payloads that leak the browser credential.
    /// </summary>
    /// <param name="value">The candidate value.</param>
    /// <returns><c>true</c> when the value appears to embed raw key material.</returns>
    public static bool LooksLikeRawKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        var prefix = Prefix.AsSpan();

        if (span.StartsWith(prefix, StringComparison.Ordinal))
        {
            return true;
        }

        // Catch the prefix appearing anywhere in a larger string (e.g. a URL).
        return value.Contains(Prefix, StringComparison.Ordinal);
    }
}
