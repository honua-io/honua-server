// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
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
                // PERFORMANCE OPTIMIZATION: Use bbox operator && for fast spatial index filtering
                filterGeometry = BuildSpatialFilterGeometryExpression(filter, query, ref paramIndex, parameters);
                clause = $"{geometryOperand} && {filterGeometry} AND ST_Intersects({geometryOperand}, {filterGeometry})";
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
                // Use ST_DWithin with geography type for accurate geodesic distance calculations
                var geographyFilter = BuildGeographyFilterExpression(filter, query, ref paramIndex);
                parameters?.Add(filter.Geometry);
                clause = $"ST_DWithin({geographyOperand}, {geographyFilter}, ${paramIndex++})";
                if (parameters != null)
                {
                    var distanceInMeters = _geometryProcessor.ConvertDistanceToMeters(filter.Distance ?? 0, filter.DistanceUnit);
                    parameters.Add(distanceInMeters);
                }
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
}
