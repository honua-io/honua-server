// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Validates an ArcGIS OAuth2 <c>redirect_uri</c> against the per-deployment
/// allow-list (#1484). This is the open-redirect mitigation for the
/// <c>oauth2/authorize</c> entrypoint: before this gate the bridge accepted any
/// absolute URI, which an attacker could use to bounce a victim's authorization
/// code (and, by extension, a freshly minted portal token) to an arbitrary host.
/// </summary>
/// <remarks>
/// <para>
/// Matching is deliberately conservative. An allow-list entry matches a candidate
/// redirect URI when either:
/// </para>
/// <list type="bullet">
/// <item><description>the candidate equals the entry exactly (ordinal, after a
/// fragment is stripped — RFC 6749 §3.1.2 forbids fragments in redirect URIs), or</description></item>
/// <item><description>the entry is an <em>origin</em> (an absolute http/https URI
/// whose path is empty or <c>/</c> and which carries no query/fragment) and the
/// candidate shares that exact scheme, host, and port.</description></item>
/// </list>
/// <para>
/// Non-http(s) schemes (for example the ArcGIS Pro native
/// <c>urn:ietf:wg:oauth:2.0:oob</c> redirect or a custom app scheme) only ever
/// match by exact, verbatim equality so an operator must opt in to them
/// explicitly. No substring, prefix, or wildcard matching is performed.
/// </para>
/// </remarks>
public static class PortalOAuthRedirectUriValidator
{
    /// <summary>
    /// Determines whether <paramref name="redirectUri"/> is permitted by the
    /// configured <paramref name="allowList"/>.
    /// </summary>
    /// <param name="redirectUri">The client-supplied redirect URI.</param>
    /// <param name="allowList">The configured allow-list entries. An empty or
    /// <see langword="null"/> list permits nothing.</param>
    /// <returns><see langword="true"/> when the redirect URI is allow-listed.</returns>
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
