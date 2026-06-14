// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.LicenseMint;

/// <summary>
/// Base64URL encode/decode helpers matching the runtime verifier's expectations
/// (RFC 4648 §5, unpadded). The verifier decodes the envelope <c>payload</c> and
/// <c>signature</c> as Base64URL, so the mint side must emit the same form.
/// </summary>
internal static class Base64Url
{
    /// <summary>Encodes bytes as unpadded Base64URL.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Decodes an unpadded (or padded) Base64URL string.</summary>
    public static byte[] Decode(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        var padding = (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid Base64URL length.")
        };

        return Convert.FromBase64String(normalized + padding);
    }
}
