// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Infrastructure.Logging;

// Control characters are stripped so user input cannot forge a second log line, and the correlation token does not reveal the secret.
public static class LogValueRedactor
{
    public const int DefaultMaxSanitizedLength = 256;

    private const int HashHexLength = 8;

    private const string EmptyHashToken = "00000000";

    private const char SanitizedReplacement = '_';

    public static string Hash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return EmptyHashToken;
        }

        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> input = maxBytes <= 512
            ? stackalloc byte[512]
            : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));

        try
        {
            var byteLen = Encoding.UTF8.GetBytes(value, input);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(input[..byteLen], hash);
            return Convert.ToHexString(hash[..(HashHexLength / 2)]).ToLowerInvariant();
        }
        finally
        {
            if (rented is not null)
            {
                // Clear before returning: the rented buffer just held a UTF-8 copy of a
                // sensitive value (bearer token, api key, session id) and would otherwise
                // remain readable by the next consumer of the shared ArrayPool.
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    public static string SanitizeForLog(string? value, int maxLength = DefaultMaxSanitizedLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0)
        {
            return string.Empty;
        }

        var source = value.Length > maxLength ? value.AsSpan(0, maxLength) : value.AsSpan();
        char[]? rented = null;
        Span<char> buffer = source.Length <= 256
            ? stackalloc char[256]
            : (rented = ArrayPool<char>.Shared.Rent(source.Length));

        try
        {
            var written = 0;
            foreach (var c in source)
            {
                buffer[written++] = IsControl(c) ? SanitizedReplacement : c;
            }

            return new string(buffer[..written]);
        }
        finally
        {
            if (rented is not null)
            {
                // Clear before returning: the rented buffer just held a copy of a
                // user-controlled (potentially sensitive) value and would otherwise
                // remain readable by the next consumer of the shared ArrayPool.
                ArrayPool<char>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    private static bool IsControl(char c) => c < ' ' || c == '';
}
