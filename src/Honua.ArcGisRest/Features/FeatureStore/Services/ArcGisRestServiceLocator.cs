// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Security.Domain;

namespace Honua.ArcGisRest.Features.FeatureStore.Services;

/// <summary>
/// Resolves the remote ArcGIS REST service URL and optional API token from a
/// <see cref="DataConnection"/>. The provider treats the connection string as
/// the canonical service root URL (FeatureServer or MapServer) and reads an
/// optional <c>token</c> property from <see cref="DataConnection.Properties"/>.
/// </summary>
internal static class ArcGisRestServiceLocator
{
    /// <summary>
    /// Connection properties key carrying an ArcGIS REST API token. Optional;
    /// publicly readable services do not require it.
    /// </summary>
    public const string TokenPropertyKey = "token";

    /// <summary>
    /// Connection properties key carrying the service URL when not stored in the
    /// connection string. Useful when the connection string is reserved for
    /// secret-store routing.
    /// </summary>
    public const string ServiceUrlPropertyKey = "serviceUrl";

    // Cache of raw URL → normalized URL to avoid a blocking DNS lookup on every
    // per-query call to Resolve(). NormalizeServiceRootUrl performs DNS resolution
    // to validate the host against private-range blocklists; doing this on every
    // QueryAsync/CountAsync call blocks thread-pool threads under DNS latency.
    // The cache is process-scoped and never evicted — service URLs come from
    // admin-configured DataConnections and are stable for the lifetime of the process.
    // Thread-safe: ConcurrentDictionary ensures at most one normalization per distinct URL.
    private static readonly ConcurrentDictionary<string, string> _normalizedUrlCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the canonical service root URL (FeatureServer/MapServer) and
    /// optional API token from the supplied <paramref name="connection"/>.
    /// URL validation (including DNS-based private-range check) is performed once
    /// per distinct raw URL and cached for the lifetime of the process.
    /// </summary>
    /// <param name="connection">Secure connection bound by the metadata layer.</param>
    /// <returns>Canonical service URL and optional API token.</returns>
    /// <exception cref="InvalidOperationException">No usable URL is configured.</exception>
    /// <exception cref="ArgumentException">The configured URL is not a valid HTTPS service root URL.</exception>
    public static ArcGisRestServiceLocation Resolve(DataConnection? connection)
    {
        var rawUrl = ReadConnectionStringOrProperty(connection);
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw new InvalidOperationException(
                "ArcGIS REST provider requires a DataConnection whose ConnectionString or 'serviceUrl' property targets a FeatureServer or MapServer service root URL.");
        }

        var token = ReadStringProperty(connection, TokenPropertyKey);
        var normalized = _normalizedUrlCache.GetOrAdd(rawUrl, static u => ArcGisRestUrlValidator.NormalizeServiceRootUrl(u));
        return new ArcGisRestServiceLocation(normalized, token);
    }

    private static string? ReadConnectionStringOrProperty(DataConnection? connection)
    {
        if (connection is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            return connection.ConnectionString;
        }

        return ReadStringProperty(connection, ServiceUrlPropertyKey);
    }

    private static string? ReadStringProperty(DataConnection? connection, string key)
    {
        if (connection is null || connection.Properties.Count == 0)
        {
            return null;
        }

        if (!connection.Properties.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        // Only accept genuine string values. Coercing an arbitrary object via
        // ToString() (e.g. a JsonElement, number, or bool) would yield a
        // surprising value — for a token that means a header like "Bearer
        // System.Text.Json.JsonElement" rather than a clear failure. Returning
        // null for a non-string keeps misconfiguration diagnosable: a non-string
        // token degrades to "no token" and a non-string serviceUrl falls through
        // to the missing-URL error in Resolve.
        return raw is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
    }
}

/// <summary>
/// Resolved location of a remote ArcGIS REST service backing a federated publication.
/// </summary>
/// <param name="ServiceUrl">Canonical FeatureServer/MapServer service root URL (no trailing slash).</param>
/// <param name="Token">Optional ArcGIS API token used to authenticate requests.</param>
internal sealed record ArcGisRestServiceLocation(string ServiceUrl, string? Token);
