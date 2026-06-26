// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Postgres.Features.Geoprocessing;

/// <summary>
/// <c>source.honua-layer</c> DAG connector. Streams features from a Honua catalog
/// layer by REUSING the canonical query pipeline
/// (<see cref="IStreamingFeatureStore.StreamFeaturesAsync"/>) rather than touching
/// SQL directly, so the layer's permanent filters, paging, CRS handling, and field
/// masking are inherited. The where/bbox/since inputs are translated into a
/// <see cref="FeatureQuery"/>; the streamed WKB geometry is converted to GeoJSON
/// through the shared managed NetTopologySuite reader/writer (no native dependency).
/// </summary>
internal sealed class HonuaLayerDagSource : IDagFeatureSource
{
    private readonly IStreamingFeatureStore _streamingStore;

    public HonuaLayerDagSource(IStreamingFeatureStore streamingStore)
    {
        _streamingStore = streamingStore;
    }

    public string SourceId => "source.honua-layer";

    public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
        DagSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.LayerId is not { } layerId)
        {
            throw new InvalidOperationException("source.honua-layer requires a layerId.");
        }

        var query = BuildQuery(request);

        // Per-feature WKB -> GeoJSON conversion via the shared managed NTS reader/writer.
        var wkbReader = new WKBReader();
        var geoJsonWriter = new GeoJsonWriter();

        await foreach (var feature in _streamingStore
            .StreamFeaturesAsync(layerId, query, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? geometryGeoJson = null;
            if (feature.Geometry is { Length: > 0 } wkb)
            {
                var geometry = wkbReader.Read(wkb);
                if (geometry is not null && !geometry.IsEmpty)
                {
                    geometryGeoJson = geoJsonWriter.Write(geometry);
                }
            }

            yield return new DagSourceFeature
            {
                GeometryGeoJson = geometryGeoJson,
                Attributes = feature.Attributes
            };
        }
    }

    private static FeatureQuery BuildQuery(DagSourceRequest request)
    {
        var where = BuildWhereClause(request);

        ImmutableArray<string>? outFields = string.IsNullOrWhiteSpace(request.OutFields)
            ? null
            : request.OutFields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToImmutableArray();

        return new FeatureQuery
        {
            Where = where,
            OutFields = outFields,
            OutputSrid = request.OutputSrid,
            SpatialFilter = BuildSpatialFilter(request),
            IncludeZ = true,
            IncludeM = false
        };
    }

    private static string? BuildWhereClause(DagSourceRequest request)
    {
        var clauses = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(request.Where))
        {
            clauses.Add(request.Where);
        }

        if (!string.IsNullOrWhiteSpace(request.Since) && !string.IsNullOrWhiteSpace(request.WatermarkField))
        {
            clauses.Add($"{request.WatermarkField} >= TIMESTAMP '{request.Since!.Replace("'", "''", StringComparison.Ordinal)}'");
        }

        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }

    private static SpatialFilter? BuildSpatialFilter(DagSourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Bbox))
        {
            return null;
        }

        var parts = request.Bbox.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY))
        {
            throw new InvalidOperationException("source.honua-layer bbox must be 'minX,minY,maxX,maxY'.");
        }

        var srid = request.OutputSrid ?? 4326;
        var factory = new GeometryFactory(new PrecisionModel(), srid);
        var envelope = factory.ToGeometry(new Envelope(minX, maxX, minY, maxY));
        var wkb = new WKBWriter().Write(envelope);
        return SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid, isSimpleEnvelope: true);
    }
}
