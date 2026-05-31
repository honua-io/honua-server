// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.ArcGisRest.Features.FeatureStore.Services;

/// <summary>
/// Validates and normalizes outbound ArcGIS REST service URLs. Mirrors the
/// surface the import-side <c>ArcGisRestClient</c> uses so federated read-through
/// queries cannot resolve to embedded credentials, loopback hosts, or non-service
/// URLs.
/// </summary>
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
    /// non-HTTPS, contains embedded credentials, targets a loopback host, or
    /// does not point at a FeatureServer/MapServer service root.</exception>
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

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            throw new ArgumentException(
                "ArcGIS service URL must not target a loopback host.",
                nameof(url));
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

    private static bool IsLocalhostHostName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
