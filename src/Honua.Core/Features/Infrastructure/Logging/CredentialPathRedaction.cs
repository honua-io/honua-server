// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Logging;

/// <summary>
/// Redacts capability tokens that travel in URL paths. The same patterns are
/// applied to request logs and to exported trace span attributes.
/// </summary>
public static partial class CredentialPathRedaction
{
    /// <summary>Replacement text for a redacted path segment.</summary>
    public const string Redacted = "[REDACTED]";

    /// <summary>
    /// Replaces the token segment of a public-link, embed, or WPS conformance
    /// result path. Other text is returned unchanged. A match timeout redacts
    /// the whole value.
    /// </summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        try
        {
            return CredentialRoute().Replace(value, "$1" + Redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            return Redacted;
        }
    }

    [GeneratedRegex(
        @"((?:/api/v[0-9]+(?:\.[0-9]+)?/console/share/(?:link|embed)|/wps/conformance/results)/)[^/?#\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex CredentialRoute();
}
