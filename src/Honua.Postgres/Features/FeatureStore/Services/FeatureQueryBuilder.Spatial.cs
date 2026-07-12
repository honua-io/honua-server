// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private string BuildSpatialFilterGeometryExpression(
        SpatialFilter filter,
        FeatureQuery query,
        ref int paramIndex,
        List<object>? parameters)
    {
        var geometryExpression = _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);
        parameters?.Add(filter.Geometry);
        return geometryExpression;
    }

    private string BuildGeographyFilterExpression(SpatialFilter filter, FeatureQuery query, ref int paramIndex)
    {
        var geometryExpression = _geometryProcessor.BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex);

        var wgs84Srid = SpatialReference.WGS84.Wkid;
        if (query.SpatialReferenceSrid.HasValue && query.SpatialReferenceSrid.Value != wgs84Srid)
        {
            geometryExpression = $"ST_Transform({geometryExpression}, {wgs84Srid})";
        }

        return $"{geometryExpression}::geography";
    }

    private void AppendSpatialFilter(
        StringBuilder sql,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        ref int paramIndex,
        List<object>? parameters = null)
    {
        if (!query.SpatialFilter.HasValue)
        {
            return;
        }

        var filter = query.SpatialFilter.Value;
        var geometryOperand = _geometryProcessor.GetGeometryOperand(geometryStorageType, layerSrid: query.SpatialReferenceSrid);
        var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
        string? filterGeometry = null;
        string? clause = null;

        switch (filter.SpatialRelationship)
        {
            case SpatialRelationship.Intersects:
                // Use bbox operator && for fast spatial index filtering, then exact-check as needed.
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                if (CanUseEnvelopeOnlyPointIntersects(query, filter))
                {
                    clause = $"{geometryOperand} && {filterGeometry}";
                }
                else if (CanUseExactPointEnvelopeIntersects(query, filter))
                {
                    clause = BuildExactPointEnvelopeIntersectsClause(
                        geometryOperand,
                        filterGeometry,
                        filter,
                        ref paramIndex,
                        parameters);
                }
                else
                {
                    clause = $"{geometryOperand} && {filterGeometry} AND ST_Intersects({geometryOperand}, {filterGeometry})";
                }
                break;

            case SpatialRelationship.Within:
                // Esri semantics: esriSpatialRelWithin = filter geometry is within feature geometry.
                // PostGIS: ST_Within(filter, feature) = filter is within feature.
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry} AND ST_Within({filterGeometry}, {geometryOperand})";
                break;

            case SpatialRelationship.Contains:
                // Esri semantics: esriSpatialRelContains = filter geometry contains feature geometry.
                // PostGIS: ST_Contains(filter, feature) = filter contains feature.
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry} AND ST_Contains({filterGeometry}, {geometryOperand})";
                break;

            case SpatialRelationship.EnvelopeIntersects:
                // Already optimized - pure index operation
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry}";
                break;

            case SpatialRelationship.Crosses:
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry} AND ST_Crosses({geometryOperand}, {filterGeometry})";
                break;

            case SpatialRelationship.Touches:
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry} AND ST_Touches({geometryOperand}, {filterGeometry})";
                break;

            case SpatialRelationship.Overlaps:
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry} AND ST_Overlaps({geometryOperand}, {filterGeometry})";
                break;

            case SpatialRelationship.Disjoint:
                // PERFORMANCE NOTE: Disjoint operations cannot effectively use spatial indexes
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"ST_Disjoint({geometryOperand}, {filterGeometry})";
                break;

            case SpatialRelationship.Equals:
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry} AND ST_Equals({geometryOperand}, {filterGeometry})";
                break;

            case SpatialRelationship.WithinDistance:
                // Use ST_DWithin with geography type for accurate geodesic distance calculations.
                // The geography operands wrap the column in ST_Transform(col,4326)::geography, which
                // is not GiST-index-usable and forces a full scan. Pair it with an && envelope
                // pre-filter in the STORED CRS (mirroring the Intersects family) so the spatial
                // index prunes candidates before the exact geodesic check runs. (#2740)
                var withinDistanceMeters = _geometryProcessor.ConvertDistanceToMeters(filter.Distance ?? 0, filter.DistanceUnit);
                var storageFilterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                var distancePrefilter = BuildDistanceEnvelopePrefilter(
                    geometryOperand, storageFilterGeometry, withinDistanceMeters, query.SpatialReferenceSrid);
                var geographyFilter = BuildGeographyFilterExpression(filter, query, ref paramIndex);
                parameters?.Add(filter.Geometry);
                clause = $"{distancePrefilter} AND ST_DWithin({geographyOperand}, {geographyFilter}, ${paramIndex++})";
                parameters?.Add(withinDistanceMeters);
                break;

            case SpatialRelationship.BeyondDistance:
                // ST_Distance > threshold for features beyond a certain distance
                var geographyFilterDistance = BuildGeographyFilterExpression(filter, query, ref paramIndex);
                parameters?.Add(filter.Geometry);
                clause = $"ST_Distance({geographyOperand}, {geographyFilterDistance}) > ${paramIndex++}";
                if (parameters != null)
                {
                    var distanceInMeters = _geometryProcessor.ConvertDistanceToMeters(filter.Distance ?? 0, filter.DistanceUnit);
                    parameters.Add(distanceInMeters);
                }
                break;

            case SpatialRelationship.NearestNeighbor:
                // KNN uses ORDER BY with PostGIS <-> operator (handled separately)
                clause = $"{geometryOperand} IS NOT NULL";
                break;

            default:
                // PERFORMANCE OPTIMIZATION: Default to bbox + intersects for best performance
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry} AND ST_Intersects({geometryOperand}, {filterGeometry})";
                break;
        }

        if (clause == null)
        {
            return;
        }

        if (query.IncludeNullGeometry && filter.SpatialRelationship != SpatialRelationship.NearestNeighbor)
        {
            sql.Append(CultureInfo.InvariantCulture,
                $" AND ({clause} OR {DatabaseSchema.GeometryColumn} IS NULL)");
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture, $" AND {clause}");
        }
    }

    private static bool CanUseEnvelopeOnlyPointIntersects(FeatureQuery query, SpatialFilter filter)
        => filter.IsSimpleEnvelope &&
           filter.AllowEnvelopeOnly &&
           query.GeometryType == MetadataV2GeometryType.Point &&
           !query.IncludeNullGeometry;

    private static bool CanUseExactPointEnvelopeIntersects(FeatureQuery query, SpatialFilter filter)
        => filter.IsSimpleEnvelope &&
           query.GeometryType == MetadataV2GeometryType.Point &&
           !query.IncludeNullGeometry &&
           filter.EnvelopeMinX.HasValue &&
           filter.EnvelopeMinY.HasValue &&
           filter.EnvelopeMaxX.HasValue &&
           filter.EnvelopeMaxY.HasValue &&
           (!filter.Srid.HasValue ||
            !query.SpatialReferenceSrid.HasValue ||
            filter.Srid.Value == query.SpatialReferenceSrid.Value);

    private static string BuildExactPointEnvelopeIntersectsClause(
        string geometryOperand,
        string filterGeometry,
        SpatialFilter filter,
        ref int paramIndex,
        List<object>? parameters)
    {
        var minXParam = paramIndex++;
        var minYParam = paramIndex++;
        var maxXParam = paramIndex++;
        var maxYParam = paramIndex++;

        if (parameters != null)
        {
            parameters.Add(filter.EnvelopeMinX!.Value);
            parameters.Add(filter.EnvelopeMinY!.Value);
            parameters.Add(filter.EnvelopeMaxX!.Value);
            parameters.Add(filter.EnvelopeMaxY!.Value);
        }

        return $"{geometryOperand} && {filterGeometry} AND CASE " +
               $"WHEN GeometryType({geometryOperand}) = 'POINT' THEN " +
               $"ST_X({geometryOperand}) >= ${minXParam} AND " +
               $"ST_Y({geometryOperand}) >= ${minYParam} AND " +
               $"ST_X({geometryOperand}) <= ${maxXParam} AND " +
               $"ST_Y({geometryOperand}) <= ${maxYParam} " +
               $"ELSE ST_Intersects({geometryOperand}, {filterGeometry}) END";
    }

    /// <summary>
    /// Builds an index-usable <c>&amp;&amp;</c> bounding-box pre-filter for a WithinDistance predicate.
    /// The envelope is the filter geometry (already in the stored column's CRS) expanded by the
    /// search distance converted to the storage CRS's units, so the GiST index on the geometry
    /// column can prune candidates before the exact geodesic <c>ST_DWithin(...::geography)</c> runs.
    /// The pre-filter is deliberately conservative (over-expanding rather than under-expanding); the
    /// exact geography predicate still decides membership, so the envelope can never exclude a real
    /// match. Only WithinDistance uses this — BeyondDistance selects features outside the envelope,
    /// where a bounding-box window would incorrectly drop the desired far features.
    /// </summary>
    private static string BuildDistanceEnvelopePrefilter(
        string geometryOperand,
        string storageFilterGeometry,
        double distanceInMeters,
        int? spatialReferenceSrid)
    {
        var storageSrid = spatialReferenceSrid ?? SpatialReference.WGS84.Wkid;

        // Bias classification toward "geographic" only for the curated list plus the unlisted
        // EPSG geographic 2D range (4000-4999). Misclassifying projected-metre storage as
        // geographic would under-expand (degrees << metres) and drop matches, so everything else
        // is treated as metre-unit projected storage. Full CRS-unit resolution is deferred to #2732.
        var isGeographicStorage =
            DistanceConversions.IsGeographicSrid(storageSrid) || IsUnlistedGeographicSridRange(storageSrid);

        string expansion;
        if (isGeographicStorage)
        {
            // Geographic storage measures the envelope in degrees: ~111320 m per degree of
            // latitude, and ~111320*cos(lat) m per degree of longitude. Expanding by only
            // metres/111320 would under-cover east/west at high latitudes and could drop true
            // matches, so divide by cos(lat) using the filter geometry's own latitude extent,
            // clamped to 89.9deg to bound the blow-up near the poles. Over-expansion only costs
            // index selectivity; correctness is preserved by the exact geography ST_DWithin.
            var degrees = (distanceInMeters / 111320.0).ToString("R", CultureInfo.InvariantCulture);
            var latExtent =
                $"LEAST(89.9, GREATEST(abs(ST_YMin({storageFilterGeometry})), abs(ST_YMax({storageFilterGeometry}))))";
            expansion = $"({degrees} / cos(radians({latExtent})))";
        }
        else
        {
            // Projected storage is assumed to use metre units (the dominant case and consistent
            // with the geography exact predicate, which always yields metres). Non-metre projected
            // CRSs are out of scope for this minimal fix (#2732).
            expansion = distanceInMeters.ToString("R", CultureInfo.InvariantCulture);
        }

        return $"{geometryOperand} && ST_Expand({storageFilterGeometry}, {expansion})";
    }

    private static bool IsUnlistedGeographicSridRange(int srid)
        => srid is >= 4000 and <= 4999 && !DistanceConversions.IsGeographicSrid(srid);
}
