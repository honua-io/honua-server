// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Sanitizes error messages to avoid exposing sensitive data in recent error summaries.
/// </summary>
internal static class RecentErrorSanitizer
{
    private const int MaxMessageLength = 240;

    private static readonly Regex _emailPattern = new(
        "\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex _keyValuePattern = new(
        "(?i)\\b(password|pwd|secret|token|api[-_]?key|access[-_]?key|client[-_]?secret)\\b\\s*([:=])\\s*([^;\\s]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex _authorizationPattern = new(
        "(?i)\\bauthorization\\b\\s*([:=])\\s*([^;\\s]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex _bearerPattern = new(
        "(?i)\\bbearer\\s+[A-Za-z0-9-._~+/]+=*",
        RegexOptions.CultureInvariant);

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unspecified error";
        }

        var sanitized = message.Trim();
        if (sanitized.Length > MaxMessageLength)
        {
            sanitized = sanitized[..MaxMessageLength] + "...";
        }

        sanitized = _emailPattern.Replace(sanitized, "***");
        sanitized = _keyValuePattern.Replace(sanitized, "$1$2***");
        sanitized = _authorizationPattern.Replace(sanitized, "Authorization$1***");
        sanitized = _bearerPattern.Replace(sanitized, "Bearer ***");

        return sanitized;
    }
}
