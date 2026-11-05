// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

// Transport and HTTP failures propagate so the executor counts them toward the per-source timeout and circuit breaker.
internal abstract class HttpGeoJsonFederatedSourceConnector : IFederatedSourceConnector
{
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

    protected HttpGeoJsonFederatedSourceConnector(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public abstract FederatedSourceKind Kind { get; }

    protected abstract Uri BuildRequestUri(FederatedFetchRequest request);

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

    // Remote request numbers use the invariant culture so a locale cannot change the decimal separator.
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
        var keyIndex = 0;
        while (keyIndex < IdentifierKeys.Length)
        {
            if (attributes.TryGetValue(IdentifierKeys[keyIndex], out var value) &&
                TryConvertToInt64(value, out var id))
            {
                return id;
            }

            keyIndex++;
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
