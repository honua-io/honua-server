// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    // WGS 84 lower bounds on arc length per degree: the meridian degree is shortest at the equator,
    // a(1 - e^2) * pi / 180 = 110574.27 m, and the parallel degree at latitude phi is at least
    // a * cos(phi) * pi / 180 = 111319.49 m * cos(phi). Rounded down so the expansion only grows.
    private const double MinMetersPerLatitudeDegree = 110574.0;
    private const double MinMetersPerEquatorialLongitudeDegree = 111319.0;

    // Head-room over the requested radius for floating-point and spheroid-tolerance differences.
    private const double DistanceReachFactor = 1.001;
    private const double DistanceReachSlackMeters = 1.0;

    // Geodesic filter edges are densified at the radius (at least this long) before the vertex box
    // is taken, adding half a segment to the reach of line and polygon filters.
    private const double MinFilterSegmentMeters = 1000.0;

    // Transformed probes: segments per box edge, relative margin for the densification, the widest
    // expansion still transformed (wider reaches risk leaving the projection's domain), and the
    // ordinate of the admit-everything envelope (finite in the float4 GiST keys).
    private const int ProjectedEnvelopeSegments = 64;
    private const double ProjectedEnvelopeMargin = 0.01;
    private const double MaxTransformedReachDegrees = 10.0;
    private const double UnboundedEnvelopeOrdinate = 1e30;

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
                    clause = IndexedAnd(
                        geometryOperand,
                        filterGeometry,
                        filter.AntimeridianSplit,
                        $"ST_Intersects({geometryOperand}, {filterGeometry})");
                }
                break;

            case SpatialRelationship.Within:
                // Esri semantics: esriSpatialRelWithin = filter geometry is within feature geometry.
                // PostGIS: ST_Within(filter, feature) = filter is within feature.
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = IndexedAnd(
                    geometryOperand,
                    filterGeometry,
                    filter.AntimeridianSplit,
                    $"ST_Within({filterGeometry}, {geometryOperand})");
                break;

            case SpatialRelationship.Contains:
                // Esri semantics: esriSpatialRelContains = filter geometry contains feature geometry.
                // PostGIS: ST_Contains(filter, feature) = filter contains feature.
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = IndexedAnd(
                    geometryOperand,
                    filterGeometry,
                    filter.AntimeridianSplit,
                    $"ST_Contains({filterGeometry}, {geometryOperand})");
                break;

            case SpatialRelationship.EnvelopeIntersects:
                // Already optimized - pure index operation. A date-line split must probe each
                // half; the union box of the two halves covers every longitude.
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = IndexProbe(geometryOperand, filterGeometry, filter.AntimeridianSplit);
                break;

            case SpatialRelationship.Crosses:
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = IndexedAnd(
                    geometryOperand,
                    filterGeometry,
                    filter.AntimeridianSplit,
                    $"ST_Crosses({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Touches:
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = IndexedAnd(
                    geometryOperand,
                    filterGeometry,
                    filter.AntimeridianSplit,
                    $"ST_Touches({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Overlaps:
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = IndexedAnd(
                    geometryOperand,
                    filterGeometry,
                    filter.AntimeridianSplit,
                    $"ST_Overlaps({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.Disjoint:
                // PERFORMANCE NOTE: Disjoint operations cannot effectively use spatial indexes
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"ST_Disjoint({geometryOperand}, {filterGeometry})";
                break;

            case SpatialRelationship.Equals:
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = IndexedAnd(
                    geometryOperand,
                    filterGeometry,
                    filter.AntimeridianSplit,
                    $"ST_Equals({geometryOperand}, {filterGeometry})");
                break;

            case SpatialRelationship.WithinDistance:
                // Use ST_DWithin with geography type for accurate geodesic distance calculations.
                // The geography operands wrap the column in ST_Transform(col,4326)::geography, which
                // is not GiST-index-usable and forces a full scan. Pair it with an && envelope
                // pre-filter in the STORED CRS (mirroring the Intersects family) so the spatial
                // index prunes candidates before the exact geodesic check runs. (#2740)
                var withinDistanceMeters = _geometryProcessor.ConvertDistanceToMeters(filter.Distance ?? 0, filter.DistanceUnit);
                var storageFilterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                var geographyFilter = BuildGeographyFilterExpression(filter, query, ref paramIndex);
                parameters?.Add(filter.Geometry);
                var distancePrefilter = BuildDistanceEnvelopePrefilter(
                    geometryOperand, storageFilterGeometry, geographyFilter, withinDistanceMeters, query.SpatialReferenceSrid);
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
                clause = IndexedAnd(
                    geometryOperand,
                    filterGeometry,
                    filter.AntimeridianSplit,
                    $"ST_Intersects({geometryOperand}, {filterGeometry})");
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
    /// The exact <c>ST_DWithin(...::geography)</c> check runs only on rows the box admits, so the box
    /// must contain the whole geodesic neighbourhood of the filter or true matches are lost (#5461).
    /// A metre expansion in the storage CRS cannot promise that: projections scale distances (Web
    /// Mercator doubles them at latitude 60), a spherical degree overstates the equatorial meridian
    /// degree, and longitudes converge at the poles.
    /// </summary>
    /// <remarks>
    /// The box is derived on the WGS 84 ellipsoid from the same geography the exact check uses. Along
    /// any geodesic of length d, latitude changes by at most d / M(0) (the meridian radius is smallest
    /// at the equator) and longitude by at most d / (a cos(phi)) while |phi| stays below the reach
    /// latitude (N(phi) is never below a), so the lat/lon box around the filter expanded by those
    /// amounts holds every match. Filter edges are great-circle arcs that can bulge poleward of their
    /// vertices; segmentizing them puts every edge point within half a segment of a vertex, so the
    /// reach grows by that half for non-point filters. A reach past a pole spans all longitudes.
    /// WGS 84 storage probes the box directly, shifted by +/-360 degrees for the antimeridian. Other
    /// storage CRSes probe the bounding box of the densified box transformed into the stored CRS,
    /// with a margin for the densification; when the box crosses the antimeridian or a pole, or is
    /// too wide to transform reliably, the probe admits every geometry instead of guessing. The
    /// probe is one uncorrelated scalar subquery, evaluated once per statement.
    /// </remarks>
    private static string BuildDistanceEnvelopePrefilter(
        string geometryOperand,
        string storageFilterGeometry,
        string geographyFilter,
        double distanceInMeters,
        int? spatialReferenceSrid)
    {
        var storageSrid = spatialReferenceSrid ?? SpatialReference.WGS84.Wkid;
        var reach = Sql(distanceInMeters * DistanceReachFactor + DistanceReachSlackMeters);
        var segment = Sql(Math.Max(distanceInMeters, MinFilterSegmentMeters));
        var metresPerLatitudeDegree = Sql(MinMetersPerLatitudeDegree);
        var metresPerLongitudeDegree = Sql(MinMetersPerEquatorialLongitudeDegree);
        var storageSridSql = $"ST_SRID({storageFilterGeometry})";

        var neighbourhood =
            "SELECT n.vertices, n.lat_reach, n.dlat, " +
            $"CASE WHEN n.lat_reach >= 90 THEN 360 ELSE LEAST(360, n.reach / ({metresPerLongitudeDegree} * cos(radians(n.lat_reach)))) END AS dlon " +
            "FROM (SELECT v.vertices, v.reach, " +
            $"v.reach / {metresPerLatitudeDegree} AS dlat, " +
            $"GREATEST(abs(ST_YMin(v.vertices)), abs(ST_YMax(v.vertices))) + v.reach / {metresPerLatitudeDegree} AS lat_reach " +
            $"FROM (SELECT g.vertices, {reach} + CASE WHEN ST_Dimension(g.vertices) = 0 THEN 0 ELSE {segment} / 2.0 END AS reach " +
            $"FROM (SELECT ST_Segmentize({geographyFilter}, {segment})::geometry AS vertices) g) v) n";
        var box = $"SELECT b.lat_reach, b.dlat, b.dlon, ST_Expand(b.vertices, b.dlon, b.dlat) AS box FROM ({neighbourhood}) b";

        if (storageSrid == SpatialReference.WGS84.Wkid)
        {
            var envelope = $"(SELECT ST_SetSRID(e.box, {storageSridSql}) FROM ({box}) e)";
            return $"({geometryOperand} && {envelope}" +
                   $" OR {geometryOperand} && ST_Translate({envelope}, 360, 0)" +
                   $" OR {geometryOperand} && ST_Translate({envelope}, -360, 0))";
        }

        var maxDegrees = Sql(MaxTransformedReachDegrees);
        var unbounded = Sql(UnboundedEnvelopeOrdinate);
        var transformed =
            "SELECT CASE WHEN e.lat_reach >= 90 OR ST_XMin(e.box) < -180 OR ST_XMax(e.box) > 180 " +
            $"OR e.dlat > {maxDegrees} OR e.dlon > {maxDegrees} THEN NULL " +
            "ELSE ST_Envelope(ST_Transform(ST_Segmentize(e.box, " +
            $"GREATEST(ST_XMax(e.box) - ST_XMin(e.box), ST_YMax(e.box) - ST_YMin(e.box)) / {ProjectedEnvelopeSegments}), {storageSridSql})) END AS envelope " +
            $"FROM ({box}) e";
        var projectedEnvelope =
            $"(SELECT CASE WHEN t.envelope IS NULL THEN ST_MakeEnvelope(-{unbounded}, -{unbounded}, {unbounded}, {unbounded}, {storageSridSql}) " +
            $"ELSE ST_Expand(t.envelope, {Sql(ProjectedEnvelopeMargin)} * GREATEST(ST_XMax(t.envelope) - ST_XMin(t.envelope), ST_YMax(t.envelope) - ST_YMin(t.envelope))) END " +
            $"FROM ({transformed}) t)";
        return $"{geometryOperand} && {projectedEnvelope}";
    }

    private static string Sql(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string IndexProbe(string geometryOperand, string filterGeometry, bool antimeridianSplit)
        => antimeridianSplit
            ? $"({geometryOperand} && ST_GeometryN({filterGeometry}, 1) OR {geometryOperand} && ST_GeometryN({filterGeometry}, 2))"
            : $"{geometryOperand} && {filterGeometry}";

    private static string IndexedAnd(
        string geometryOperand,
        string filterGeometry,
        bool antimeridianSplit,
        string predicate)
        => $"{IndexProbe(geometryOperand, filterGeometry, antimeridianSplit)} AND {predicate}";

    private static bool IsUnlistedGeographicSridRange(int srid)
        => srid is >= 4000 and <= 4999 && !DistanceConversions.IsGeographicSrid(srid);
}
