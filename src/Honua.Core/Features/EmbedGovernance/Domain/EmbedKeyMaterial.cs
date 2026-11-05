// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.EmbedGovernance.Domain;

public static class EmbedKeyMaterial
{
    public const string Prefix = "embk_";

    private const int KeyByteCount = 32;
    private const int DisplayPrefixLength = 14;

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeyByteCount);
        return Prefix + Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string DisplayPrefix(string keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        var length = Math.Min(DisplayPrefixLength, keyMaterial.Length);
        return keyMaterial[..length];
    }

    public static byte[] Hash(string keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        return SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
    }

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
