// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Infrastructure.Logging;

/// <summary>
/// AOT-safe helpers for redacting user-controlled values that flow into log lines.
/// </summary>
/// <remarks>
/// Two operations are exposed:
/// <list type="bullet">
///   <item><description><see cref="Hash"/> produces a short, irreversible correlation token suitable for grouping
///   events that share an underlying secret (api key, session id, cache key) without exposing the secret itself.</description></item>
///   <item><description><see cref="SanitizeForLog"/> strips CR / LF and other control characters that would otherwise
///   allow user input to forge or split log lines, and truncates to a bounded length.</description></item>
/// </list>
/// Both operations are pure, allocation-bounded, and reflection-free so they remain trim / Native AOT compatible.
/// Exposed as <see langword="public"/> so consuming projects (Honua.Server, Honua.Postgres, etc.) can redact
/// log values without each project owning a copy of the helper.
/// </remarks>
public static class LogValueRedactor
{
    /// <summary>
    /// Default cap for a sanitized log fragment in characters. Long enough to retain meaningful context for valid
    /// values (schema names, family labels) and short enough to bound log volume on adversarial input.
    /// </summary>
    public const int DefaultMaxSanitizedLength = 256;

    private const int HashHexLength = 8;

    private const string EmptyHashToken = "00000000";

    private const char SanitizedReplacement = '_';

    /// <summary>
    /// Returns a short, irreversible SHA-256 prefix (8 hex chars) of the supplied value for log correlation.
    /// </summary>
    /// <param name="value">User-controlled or sensitive value to redact.</param>
    /// <returns>A lowercase hexadecimal token of length <see cref="HashHexLength"/>, or <c>"00000000"</c> when the
    /// input is null or empty.</returns>
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
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="value"/> with CR / LF and other control characters replaced by
    /// <c>'_'</c>, and the result truncated to <paramref name="maxLength"/> characters.
    /// </summary>
    /// <param name="value">User-controlled value that may flow into a log line.</param>
    /// <param name="maxLength">Maximum length of the returned string. Defaults to
    /// <see cref="DefaultMaxSanitizedLength"/>.</param>
    /// <returns>A bounded, control-character-free representation suitable for direct inclusion in log output.</returns>
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
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private static bool IsControl(char c) => c < ' ' || c == '';
}
