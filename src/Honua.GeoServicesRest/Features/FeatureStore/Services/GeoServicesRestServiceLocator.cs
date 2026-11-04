// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Security.Domain;

namespace Honua.GeoServicesRest.Features.FeatureStore.Services;

internal static class GeoServicesRestServiceLocator
{
    public const string TokenPropertyKey = "token";

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

    public static GeoServicesRestServiceLocation Resolve(DataConnection? connection)
    {
        var rawUrl = ReadConnectionStringOrProperty(connection);
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw new InvalidOperationException(
                "ArcGIS REST provider requires a DataConnection whose ConnectionString or 'serviceUrl' property targets a FeatureServer or MapServer service root URL.");
        }

        var token = ReadStringProperty(connection, TokenPropertyKey);
        var normalized = _normalizedUrlCache.GetOrAdd(rawUrl, static u => GeoServicesRestUrlValidator.NormalizeServiceRootUrl(u));
        return new GeoServicesRestServiceLocation(normalized, token);
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

internal sealed record GeoServicesRestServiceLocation(string ServiceUrl, string? Token);
