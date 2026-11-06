// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.LicenseMint;

// Payload and signature use unpadded Base64URL (RFC 4648 section 5) so the mint matches the runtime verifier byte for byte.
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
