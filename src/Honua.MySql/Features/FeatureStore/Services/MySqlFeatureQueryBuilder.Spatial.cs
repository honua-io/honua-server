// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Features.FeatureStore.Services;

internal sealed partial class MySqlFeatureQueryBuilder
{
    private void AppendSpatialFilter(
        StringBuilder sb,
        MySqlLayerMapping mapping,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.SpatialFilter is not { } filter)
        {
            return;
        }

        if (filter.Srid.HasValue && filter.Srid.Value > 0 && filter.Srid.Value != mapping.Srid)
        {
            throw new NotSupportedException(
                $"Cross-SRID spatial filters are not supported by the MySQL/MariaDB provider " +
                $"(layer SRID is {mapping.Srid}, filter SRID is {filter.Srid.Value}). " +
                $"Pre-project filter geometries to the layer SRID before querying.");
        }

        if (filter.SpatialRelationship == SpatialRelationship.NearestNeighbor)
        {
            throw new NotSupportedException(
                "Nearest-neighbor (KNN) spatial queries are not supported by the MySQL/MariaDB provider. " +
                "Check FeatureProviderCapabilities.SupportsNearestNeighbor before invoking.");
        }

        var geomCol = mapping.QuotedGeometryColumn;
        var filterGeom = AppendFilterGeometryParam(filter, mapping, _engineFlavor, ref paramIndex, parameters);

        var clause = filter.SpatialRelationship switch
        {
            // MBRIntersects is index-eligible (uses SPATIAL index when available); ST_Intersects
            // refines the candidates to exact intersection. MySQL/MariaDB plan this combo well.
            SpatialRelationship.Intersects =>
                $"MBRIntersects({geomCol}, {filterGeom}) AND ST_Intersects({geomCol}, {filterGeom})",
            SpatialRelationship.EnvelopeIntersects =>
                $"MBRIntersects({geomCol}, {filterGeom})",
            SpatialRelationship.Within =>
                $"ST_Within({geomCol}, {filterGeom})",
            SpatialRelationship.Contains =>
                $"ST_Contains({geomCol}, {filterGeom})",
            SpatialRelationship.Crosses =>
                $"ST_Crosses({geomCol}, {filterGeom})",
            SpatialRelationship.Touches =>
                $"ST_Touches({geomCol}, {filterGeom})",
            SpatialRelationship.Overlaps =>
                $"ST_Overlaps({geomCol}, {filterGeom})",
            SpatialRelationship.Disjoint =>
                $"ST_Disjoint({geomCol}, {filterGeom})",
            SpatialRelationship.Equals =>
                $"ST_Equals({geomCol}, {filterGeom})",
            SpatialRelationship.WithinDistance when filter.Distance.HasValue =>
                BuildDistanceClause(geomCol, filterGeom, filter, mapping, withinDistance: true, ref paramIndex, parameters),
            SpatialRelationship.BeyondDistance when filter.Distance.HasValue =>
                BuildDistanceClause(geomCol, filterGeom, filter, mapping, withinDistance: false, ref paramIndex, parameters),
            _ => throw new NotSupportedException(
                $"Spatial relationship '{filter.SpatialRelationship}' is not supported by the MySQL/MariaDB provider.")
        };

        sb.Append(CultureInfo.InvariantCulture, $" AND {clause}");
    }

    private static string AppendFilterGeometryParam(
        SpatialFilter filter,
        MySqlLayerMapping mapping,
        MySqlEngineFlavor engineFlavor,
        ref int paramIndex,
        List<object> parameters)
    {
        var wkbParam = $"@p{paramIndex++}";
        parameters.Add(filter.Geometry);

        // ST_GeomFromWKB parses canonical X/Y WKB and tags the result with the layer SRID so
        // MySQL's spatial functions reject mismatched references at evaluation time. On MySQL
        // the 'axis-order=long-lat' option keeps geographic SRSes (e.g. EPSG:4326) lon-lat
        // even though the SRS metadata declares latitude-first; MariaDB ignores SRS axis
        // order and is always WKB-natural.
        return MySqlSpatialSql.GeomFromWkb(wkbParam, mapping.Srid, engineFlavor);
    }

    private static string BuildDistanceClause(
        string geomCol,
        string filterGeom,
        SpatialFilter filter,
        MySqlLayerMapping mapping,
        bool withinDistance,
        ref int paramIndex,
        List<object> parameters)
    {
        // Distance queries require Point-typed layers because ST_Distance_Sphere is defined
        // for points; polygon/line distance would silently degrade to centroid math.
        if (mapping.GeometryType is not GeometryType.Point and not GeometryType.MultiPoint)
        {
            throw new NotSupportedException(
                $"Distance spatial filters are only supported for point layers in the MySQL/MariaDB provider " +
                $"(layer geometry is {mapping.GeometryType}). ST_Distance_Sphere expects point geometries.");
        }

        var distanceMeters = ConvertDistanceToMeters(filter.Distance!.Value, filter.DistanceUnit);
        var distanceParam = $"@p{paramIndex++}";
        parameters.Add(distanceMeters);

        // ST_Distance_Sphere is a WGS84 spherical approximation supported by both MySQL 8.0+
        // and MariaDB 10.6+. It is documented as approximate; geodesic-accurate distance is
        // a PostGIS-only path.
        var op = withinDistance ? "<=" : ">";
        return $"ST_Distance_Sphere({geomCol}, {filterGeom}) {op} {distanceParam}";
    }

    private static double ConvertDistanceToMeters(double distance, DistanceUnit unit)
        => DistanceConversions.ToMeters(distance, unit);
}
