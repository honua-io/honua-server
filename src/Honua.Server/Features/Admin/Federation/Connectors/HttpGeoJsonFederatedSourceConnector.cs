// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using CoreFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Features.Admin.Federation.Connectors;

/// <summary>
/// Base <see cref="IFederatedSourceConnector"/> for HTTP transports whose query response is a
/// GeoJSON <c>FeatureCollection</c> (Esri ArcGIS REST with <c>f=geojson</c>, OGC API - Features
/// / WFS items). It performs the only remote I/O in the federation layer: it builds the
/// pushed-down remote request URI, issues a single GET through the named federation HTTP
/// client, parses the GeoJSON response, and maps each remote feature to a canonical
/// <see cref="CoreFeature"/> (WKB geometry + attribute table). Transport and HTTP-status
/// failures surface as exceptions so the federation executor counts them toward the per-source
/// timeout and circuit breaker (issue #341).
/// </summary>
internal abstract class HttpGeoJsonFederatedSourceConnector : IFederatedSourceConnector
{
    /// <summary>
    /// Logical name of the named <see cref="HttpClient"/> the federation connectors resolve.
    /// </summary>
    public const string HttpClientName = "honua-federation";

    // Common attribute keys that carry a stable remote identifier, in priority order. Remote
    // sources rarely agree on casing, so the lookup is exhaustive before falling back to the
    // feature's ordinal position.
    private static readonly string[] IdentifierKeys =
    [
        "objectid", "OBJECTID", "fid", "FID", "id", "ID", "gid", "GID",
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpGeoJsonFederatedSourceConnector"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the named federation HTTP client.</param>
    /// <param name="logger">Logger for transport diagnostics.</param>
    protected HttpGeoJsonFederatedSourceConnector(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public abstract FederatedSourceKind Kind { get; }

    /// <summary>
    /// Builds the remote query URI for the fetch request, applying only the predicates the
    /// plan marked as pushed down for this transport.
    /// </summary>
    /// <param name="request">The federated fetch request.</param>
    /// <returns>The absolute remote query URI.</returns>
    protected abstract Uri BuildRequestUri(FederatedFetchRequest request);

    /// <inheritdoc />
    public async Task<ImmutableArray<CoreFeature>> FetchAsync(
        FederatedFetchRequest request,
        CancellationToken cancellationToken)
    {
        var uri = BuildRequestUri(request);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // A non-success status is a transport fault: surface it so the executor's circuit
        // breaker counts it. The remote URL is intentionally not echoed to callers; it is only
        // logged for operators.
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return MapFeatures(json);
    }

    /// <summary>
    /// Appends an envelope (bbox) push-down parameter set to a query builder when the plan
    /// pushes the spatial filter down and the filter exposes a simple axis-aligned envelope.
    /// </summary>
    /// <param name="request">The fetch request.</param>
    /// <param name="envelope">The resolved envelope, when available.</param>
    /// <returns><see langword="true"/> when an envelope is available to push down.</returns>
    protected static bool TryGetPushedDownEnvelope(
        in FederatedFetchRequest request,
        out (double MinX, double MinY, double MaxX, double MaxY, int? Srid) envelope)
    {
        envelope = default;

        if (!request.ShouldPushDown(FederationPredicateKind.SpatialFilter) ||
            request.Query.SpatialFilter is not { } spatial ||
            spatial.EnvelopeMinX is not { } minX ||
            spatial.EnvelopeMinY is not { } minY ||
            spatial.EnvelopeMaxX is not { } maxX ||
            spatial.EnvelopeMaxY is not { } maxY)
        {
            return false;
        }

        envelope = (minX, minY, maxX, maxY, spatial.Srid);
        return true;
    }

    /// <summary>
    /// Formats a double using the invariant culture so the remote request never carries a
    /// locale-specific decimal separator.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The invariant-culture string.</returns>
    protected static string Invariant(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private ImmutableArray<CoreFeature> MapFeatures(string geoJson)
    {
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            return ImmutableArray<CoreFeature>.Empty;
        }

        FeatureCollection collection;
        try
        {
            collection = new GeoJsonReader().Read<FeatureCollection>(geoJson) ?? new FeatureCollection();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A malformed remote payload is a transport-level fault from the federation layer's
            // point of view; surface it so the executor classifies the source as faulted.
            FederationConnectorLog.ResponseParseFailed(_logger, Kind.ToString(), ex);
            throw new InvalidOperationException(
                $"Federated source response from a '{Kind}' source could not be parsed as GeoJSON.", ex);
        }

        if (collection.Count == 0)
        {
            return ImmutableArray<CoreFeature>.Empty;
        }

        var wkbWriter = new WKBWriter();
        var builder = ImmutableArray.CreateBuilder<CoreFeature>(collection.Count);
        var ordinal = 0;

        foreach (var feature in collection)
        {
            var attributes = MapAttributes(feature.Attributes);
            var id = ResolveIdentifier(attributes, ordinal);
            var geometry = feature.Geometry is { IsEmpty: false } geom ? wkbWriter.Write(geom) : null;

            builder.Add(new CoreFeature
            {
                Id = id,
                Geometry = geometry,
                Attributes = attributes,
            });

            ordinal++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, object?> MapAttributes(IAttributesTable? table)
    {
        if (table is null)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        var names = table.GetNames();
        if (names.Length == 0)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            builder[name] = table[name];
        }

        return builder.ToImmutable();
    }

    private static long ResolveIdentifier(ImmutableDictionary<string, object?> attributes, int ordinal)
    {
        // Not a simple filter: each key must be looked up and converted via two chained
        // TryXxx calls whose out-parameter feeds the return value, so a LINQ Where/Select
        // would not read more clearly than the short-circuiting loop below.
        foreach (var key in IdentifierKeys)
        {
            if (attributes.TryGetValue(key, out var value) && TryConvertToInt64(value, out var id))
            {
                return id;
            }
        }

        return ordinal;
    }

    private static bool TryConvertToInt64(object? value, out long result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case short s:
                result = s;
                return true;
            // Equals() (not ==) is intentional: this is an exact whole-number check (identifiers
            // must round-trip losslessly to long), not a tolerance-based geometry comparison.
            // IsFinite excludes NaN/Infinity, which Equals() would otherwise treat as self-equal.
            case double d when double.IsFinite(d) && d.Equals(Math.Floor(d)):
                result = (long)d;
                return true;
            case string str when long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
