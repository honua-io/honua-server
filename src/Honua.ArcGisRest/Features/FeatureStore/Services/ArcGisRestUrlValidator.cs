// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.ArcGisRest.Features.FeatureStore.Services;

/// <summary>
/// Validates and normalizes outbound ArcGIS REST service URLs. Mirrors the
/// surface the import-side <c>ArcGisRestClient</c> uses so federated read-through
/// queries cannot resolve to embedded credentials, loopback/private/link-local
/// hosts, or non-service URLs.
/// </summary>
/// <remarks>
/// Validation here is the first of two layers. This method rejects HTTP, embedded
/// credentials, loopback/localhost hosts, and any host supplied as an IP literal
/// in a private/reserved range. The second layer — full DNS resolution with
/// connect-time re-validation against the address allow-list — is enforced by
/// <see cref="ArcGisRestOutboundGuard.CreatePinnedDnsHttpMessageHandler"/>, which
/// also defends against DNS-rebinding and redirect-based bypasses. Hostnames that
/// resolve to disallowed addresses are therefore blocked at connect time rather
/// than here, keeping this normalization step synchronous.
/// </remarks>
internal static class ArcGisRestUrlValidator
{
    private const string InvalidServiceRootUrlMessage =
        "ArcGIS service URL must target a service root URL (FeatureServer or MapServer).";

    /// <summary>
    /// Normalizes a configured service URL into the canonical service root form,
    /// stripping query/fragment and any trailing slash.
    /// </summary>
    /// <param name="url">Configured ArcGIS service URL.</param>
    /// <returns>Canonical FeatureServer/MapServer service URL (no trailing slash).</returns>
    /// <exception cref="ArgumentException">Thrown when the URL is malformed,
    /// non-HTTPS, contains embedded credentials, targets a loopback host, is an IP
    /// literal in a private/link-local/reserved range, or does not point at a
    /// FeatureServer/MapServer service root.</exception>
    public static string NormalizeServiceRootUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException(
                "ArcGIS service URL must be a valid absolute HTTPS URL.",
                nameof(url));
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "ArcGIS service URL must use HTTPS.",
                nameof(url));
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new ArgumentException(
                "ArcGIS service URL must not include embedded credentials.",
                nameof(url));
        }

        if (uri.IsLoopback || ArcGisRestOutboundGuard.IsLocalhostHostName(uri.Host))
        {
            throw new ArgumentException(
                "ArcGIS service URL must not target a loopback host.",
                nameof(url));
        }

        // When the host is supplied as an IP literal we can reject private /
        // link-local / reserved ranges synchronously. Hostnames are resolved and
        // re-validated at connect time by the pinned-DNS handler (see class remarks).
        if (IPAddress.TryParse(uri.DnsSafeHost, out _))
        {
            try
            {
                _ = ArcGisRestOutboundGuard.ResolveAllowedAddressesAsync(
                        uri.DnsSafeHost,
                        static (_, _) => throw new InvalidOperationException("IP literals are validated without DNS resolution."),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(ex.Message, nameof(url));
            }
        }

        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (!IsServiceRootUrl(normalized))
        {
            throw new ArgumentException(InvalidServiceRootUrlMessage, nameof(url));
        }

        return normalized;
    }

    private static bool IsServiceRootUrl(string normalizedUrl)
    {
        var segments = normalizedUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var tail = segments[^1];
        return tail.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
            || tail.Equals("MapServer", StringComparison.OrdinalIgnoreCase);
    }
}
