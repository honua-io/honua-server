// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;

namespace Honua.Infrastructure.Security;

// Client-facing text is stripped of stack traces, SQL, and credential fragments here so every protocol shares one leakage policy.
internal static class ErrorMessageSanitizer
{
    internal const int DefaultMaxLength = 200;

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
