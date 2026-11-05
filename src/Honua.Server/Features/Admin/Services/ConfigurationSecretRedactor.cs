// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Server.Features.Admin.Services;

// A value is masked when its name is sensitive or the value itself looks like a credential, so a new field is covered without being added to a list.
internal static class ConfigurationSecretRedactor
{
    internal const string Mask = "***";

    // Property-name tokens that indicate the value is, or carries, a credential. The original
    // denylist (password/secret/key/token) is preserved as a subset to avoid regressing sensitivity.
    private static readonly string[] SensitiveNameTokens =
    [
        "password", "passwd", "pwd", "secret", "key", "token", "credential",
        "connectionstring", "connection", "dsn", "sas", "endpoint", "username",
        "userid", "auth", "account",
    ];

    // key=value fragment prefixes that reveal a secret embedded inside a connection/URI string.
    private static readonly string[] SecretValueKeyFragments =
    [
        "password=", "pwd=", "accountkey=", "sharedaccesskey=", "sharedsecret=",
        "secret=", "apikey=", "api_key=", "accesskey=", "access_key=",
        "token=", "authorization=", "credential=",
    ];

    // scheme://user:pass@host — credentials embedded in a URI authority.
    private static readonly Regex UriCredentialPattern = new(
        @"[a-zA-Z][a-zA-Z0-9+.\-]*://[^/\s:@]+:[^/\s:@]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool IsSensitiveName(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return false;
        }

        var name = propertyName.ToLowerInvariant();
        return SensitiveNameTokens.Any(token => name.Contains(token, StringComparison.Ordinal));
    }

    internal static bool LooksCredentialBearing(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var lowered = value.ToLowerInvariant();

        if (SecretValueKeyFragments.Any(fragment => lowered.Contains(fragment, StringComparison.Ordinal)))
        {
            return true;
        }

        return UriCredentialPattern.IsMatch(value)
            || LooksLikeConnectionString(lowered)
            || LooksLikeHighEntropySecret(value);
    }

    internal static object? Redact(string? propertyName, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (IsSensitiveName(propertyName))
        {
            return Mask;
        }

        return value is string text && LooksCredentialBearing(text) ? Mask : value;
    }

    private static bool LooksLikeConnectionString(string lowered)
    {
        // Semicolon-delimited key=value pairs anchored by a host/server/database token.
        if (!lowered.Contains(';', StringComparison.Ordinal) || !lowered.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        return lowered.Contains("server=", StringComparison.Ordinal)
            || lowered.Contains("host=", StringComparison.Ordinal)
            || lowered.Contains("data source=", StringComparison.Ordinal)
            || lowered.Contains("datasource=", StringComparison.Ordinal)
            || lowered.Contains("initial catalog=", StringComparison.Ordinal)
            || lowered.Contains("database=", StringComparison.Ordinal)
            || lowered.Contains("accountname=", StringComparison.Ordinal)
            || lowered.Contains("account=", StringComparison.Ordinal);
    }

    private static bool LooksLikeHighEntropySecret(string value)
    {
        // Conservative: a single long opaque token (no whitespace) drawn from a base64/hex/url-safe
        // charset with high per-character entropy. Long human-readable strings (which contain spaces
        // or punctuation) and short identifiers/GUIDs are intentionally not matched.
        if (value.Length < 40)
        {
            return false;
        }

        foreach (var c in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return ShannonEntropyBitsPerChar(value) >= 4.0;
    }

    private static double ShannonEntropyBitsPerChar(string value)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in value)
        {
            counts[c] = counts.TryGetValue(c, out var existing) ? existing + 1 : 1;
        }

        double entropy = 0.0;
        double length = value.Length;
        foreach (var probability in counts.Values.Select(count => count / length))
        {
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }
}
