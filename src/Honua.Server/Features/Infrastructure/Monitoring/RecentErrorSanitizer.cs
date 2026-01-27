// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Sanitizes error messages to avoid exposing sensitive data in recent error summaries.
/// </summary>
internal static class RecentErrorSanitizer
{
    private const int MaxMessageLength = 240;

    private static readonly Regex EmailPattern = new(
        "\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex KeyValuePattern = new(
        "(?i)\\b(password|pwd|secret|token|api[-_]?key|access[-_]?key|client[-_]?secret)\\b\\s*([:=])\\s*([^;\\s]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex AuthorizationPattern = new(
        "(?i)\\bauthorization\\b\\s*([:=])\\s*([^;\\s]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex BearerPattern = new(
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

        sanitized = EmailPattern.Replace(sanitized, "***");
        sanitized = KeyValuePattern.Replace(sanitized, "$1$2***");
        sanitized = AuthorizationPattern.Replace(sanitized, "Authorization$1***");
        sanitized = BearerPattern.Replace(sanitized, "Bearer ***");

        return sanitized;
    }
}
