// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    /// <summary>
    /// Resolves the canonical service root URL (FeatureServer/MapServer) and
    /// optional API token from the supplied <paramref name="connection"/>.
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
        var normalized = ArcGisRestUrlValidator.NormalizeServiceRootUrl(rawUrl);
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

        return raw switch
        {
            string s => string.IsNullOrWhiteSpace(s) ? null : s,
            _ => raw.ToString()
        };
    }
}

/// <summary>
/// Resolved location of a remote ArcGIS REST service backing a federated publication.
/// </summary>
/// <param name="ServiceUrl">Canonical FeatureServer/MapServer service root URL (no trailing slash).</param>
/// <param name="Token">Optional ArcGIS API token used to authenticate requests.</param>
internal sealed record ArcGisRestServiceLocation(string ServiceUrl, string? Token);
