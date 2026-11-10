// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Logging;

public static partial class CredentialPathRedaction
{
    public const string Redacted = "[REDACTED]";

    // A match that times out redacts the whole value.
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
