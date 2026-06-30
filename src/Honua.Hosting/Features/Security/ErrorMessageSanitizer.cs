// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Shared sanitizer for client-facing error/detail text that may originate from
/// exception messages (filter/where parsing, edit validation, transaction failures).
/// Centralizes the leakage guarantees that protocol adapters must apply consistently:
/// it strips control characters, stack-trace tails, parser diagnostics, and any
/// SQL/credential/provider-internal fragments, and caps length. When the message is
/// empty or trips the blocklist, the supplied <c>fallback</c> is returned instead.
/// </summary>
/// <remarks>
/// This is the single source of truth referenced by the GeoServices edit path, the
/// OGC API Features CQL/filter path, and other adapters so that error text reflected
/// back to callers enforces one (superset) leakage policy rather than divergent
/// per-protocol blocklists.
/// </remarks>
internal static class ErrorMessageSanitizer
{
    /// <summary>
    /// Default maximum length for a sanitized client-facing message.
    /// </summary>
    internal const int DefaultMaxLength = 200;

    /// <summary>
    /// Sanitizes a candidate client-facing error message. Returns the trimmed,
    /// length-capped message when it is safe to echo, or <paramref name="fallback"/>
    /// when the message is empty or contains an unsafe pattern.
    /// </summary>
    /// <param name="message">The raw (possibly exception-derived) message.</param>
    /// <param name="fallback">The safe message to return when the candidate is rejected.</param>
    /// <param name="maxLength">Maximum length of the returned message before truncation.</param>
    /// <returns>A sanitized message safe to return to clients.</returns>
    internal static string Sanitize(string? message, string fallback, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        var sanitized = message.Trim();

        // Drop any stack-trace tail that may have been appended to the message.
        var stackTraceIndex = sanitized.IndexOf("   at ", StringComparison.Ordinal);
        if (stackTraceIndex > 0)
        {
            sanitized = sanitized[..stackTraceIndex].Trim();
        }

        if (ContainsUnsafePattern(sanitized))
        {
            return fallback;
        }

        if (sanitized.Length > maxLength)
        {
            sanitized = string.Concat(sanitized.AsSpan(0, maxLength), "...");
        }

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    /// <summary>
    /// Determines whether a message contains any control-character, parser-diagnostic,
    /// stack-trace, SQL, credential, or provider-internal fragment that must not be
    /// echoed to clients.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <returns><see langword="true"/> when the message is unsafe to echo.</returns>
    internal static bool ContainsUnsafePattern(string message)
    {
        return message.Contains('\r') ||
               message.Contains('\n') ||
               // Parser / position diagnostics (often embed user payload fragments and internal parser state).
               message.Contains("BytePositionInLine", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("LineNumber", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Path:", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("JsonException", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Unexpected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("syntax error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("parse error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains(" at position ", StringComparison.OrdinalIgnoreCase) ||
               message.Contains(" at column ", StringComparison.OrdinalIgnoreCase) ||
               message.Contains(" at line ", StringComparison.OrdinalIgnoreCase) ||
               // Runtime / stack-trace internals.
               message.Contains("System.", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("StackTrace", StringComparison.OrdinalIgnoreCase) ||
               // SQL / provider / credential internals.
               message.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("SQLSTATE", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("password", StringComparison.OrdinalIgnoreCase);
    }
}
