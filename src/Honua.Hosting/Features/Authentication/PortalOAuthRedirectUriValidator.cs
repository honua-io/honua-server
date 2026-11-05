// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// An allow-list entry matches exactly, or any URI on a listed origin, non-http schemes match only exactly, and an empty list rejects every redirect.
public static class PortalOAuthRedirectUriValidator
{
    public static bool IsAllowed(string? redirectUri, IReadOnlyList<string>? allowList)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) || allowList is null || allowList.Count == 0)
        {
            return false;
        }

        // A redirect_uri must be an absolute URI without a fragment (RFC 6749).
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var candidate) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        var canonicalCandidate = Canonicalize(candidate);

        foreach (var entry in allowList)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();

            // Exact, verbatim match (covers custom schemes and the ArcGIS oob URN).
            if (string.Equals(trimmed, redirectUri.Trim(), StringComparison.Ordinal))
            {
                return true;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var allowed))
            {
                continue;
            }

            // Canonical absolute-URI equality (normalizes default ports / casing of
            // scheme+host) for http(s) entries.
            if (IsHttpScheme(allowed) &&
                string.Equals(Canonicalize(allowed), canonicalCandidate, StringComparison.Ordinal))
            {
                return true;
            }

            // Origin match: an http(s) entry with no meaningful path matches any
            // candidate sharing the same scheme/host/port.
            if (IsOrigin(allowed) && SameOrigin(allowed, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHttpScheme(Uri uri)
        => uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
           uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private static bool IsOrigin(Uri uri)
        => IsHttpScheme(uri) &&
           (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/") &&
           string.IsNullOrEmpty(uri.Query) &&
           string.IsNullOrEmpty(uri.Fragment);

    private static bool SameOrigin(Uri a, Uri b)
        => a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) &&
           a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) &&
           a.Port == b.Port;

    private static string Canonicalize(Uri uri)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath}{uri.Query}");
}
