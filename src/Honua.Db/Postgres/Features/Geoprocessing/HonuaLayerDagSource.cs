// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Db.Postgres.Features.Geoprocessing;

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
    private readonly IMetadataV2GraphProvider? _metadata;
    private readonly ILayerSelectionFilterTranslator? _selectionTranslator;

    public HonuaLayerDagSource(
        IStreamingFeatureStore streamingStore,
        IMetadataV2GraphProvider? metadata = null,
        ILayerSelectionFilterTranslator? selectionTranslator = null)
    {
        _streamingStore = streamingStore;
        _metadata = metadata;
        _selectionTranslator = selectionTranslator;
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

        // Fail closed on a background read that cannot be constrained to its submitter
        // (honua-server#3068). This connector is THE seam every layer-sourced geoprocessing
        // process reads a catalog layer through, so the guard covers the whole family
        // (analytics.*, generalization.*, conversion.feature-project, enrichment, and the
        // source.honua-layer connector itself) without any per-executor opt-in.
        //
        // An active job scope with no submitter snapshot means the record predates the
        // snapshot, or was written by a path that does not capture one. Reading anyway would
        // resolve no RLS predicate and an empty field mask, i.e. hand back every row and every
        // attribute; refusing is the only safe outcome.
        if (JobSecurityScope.Current is { Submitter: null })
        {
            throw new UnauthorizedAccessException(
                "This job carries no submitter security context, so its layer reads cannot be constrained to the submitting caller.");
        }

        var query = BuildQuery(request);
        MetadataV2Resource? resource = null;
        if (_metadata is not null)
        {
            var snapshot = await _metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out resource))
            {
                throw new InvalidOperationException("Source layer does not exist.");
            }
            query = query with { SpatialReferenceSrid = snapshot.ResolveStorageSrid(layerId) };
        }

        if (request.HasCanonicalSelectors)
        {
            query = await ApplyCanonicalSelectionAsync(request, resource, query, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>
    /// Applies the geometry/time selectors through the canonical
    /// <see cref="ILayerSelectionFilterTranslator"/> — the same translation the synchronous
    /// analytics endpoints use (#4624) — instead of a connector-local reinterpretation. The
    /// translated where/time predicate replaces the plain where clause (SqlFilter takes
    /// precedence over Where in the store), and the geometry filter replaces any bbox. The
    /// store still ANDs the layer's permanent filter and row-level security ahead of this
    /// selection and masks restricted fields, so the caller's selectors only ever narrow what
    /// the submitter may read.
    /// </summary>
    private async Task<FeatureQuery> ApplyCanonicalSelectionAsync(
        DagSourceRequest request,
        MetadataV2Resource? resource,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (_selectionTranslator is null || resource is null)
        {
            // Never drop a selector the caller supplied: reading without it would return a
            // broader feature set than requested.
            throw new DagSourceSelectionException(
                "'geometry' and 'time' selection filters need the canonical layer selection translator and catalog " +
                "metadata, which are not configured in this deployment; narrow the input with 'where' or 'objectIds' instead.");
        }

        if (!string.IsNullOrWhiteSpace(request.Bbox) && !string.IsNullOrWhiteSpace(request.Geometry))
        {
            throw new DagSourceSelectionException("supply either 'bbox' or 'geometry', not both.");
        }

        var translation = await _selectionTranslator.TranslateAsync(
            new LayerSelectionFilter
            {
                Where = BuildWhereClause(request),
                ObjectIds = request.ObjectIds,
                Geometry = request.Geometry,
                GeometryType = request.GeometryType,
                InSr = request.InSr,
                SpatialRel = request.SpatialRel,
                Time = request.Time,
                TimeRelation = request.TimeRelation,
            },
            resource,
            cancellationToken).ConfigureAwait(false);

        if (translation.Query is not { } selected)
        {
            throw new DagSourceSelectionException($"{translation.ErrorTitle}: {translation.ErrorDetail}");
        }

        return query with
        {
            Where = selected.Where,
            SqlFilter = selected.SqlFilter,
            ObjectIds = selected.ObjectIds,
            SpatialFilter = selected.SpatialFilter ?? query.SpatialFilter,
        };
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
            ObjectIds = BuildObjectIds(request),
            OutputSrid = request.OutputSrid,
            SpatialFilter = BuildSpatialFilter(request),
            IncludeZ = true,
            IncludeM = false
        };
    }

    private static ImmutableArray<long>? BuildObjectIds(DagSourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ObjectIds))
        {
            return null;
        }

        var builder = ImmutableArray.CreateBuilder<long>();
        foreach (var token in request.ObjectIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The executor already validated every token is a parseable long; a token
            // that still fails here is skipped rather than throwing mid-stream.
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                builder.Add(id);
            }
        }

        return builder.ToImmutable();
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
