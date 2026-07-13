// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Processes geometry operations and SQL expression generation for PostgreSQL PostGIS
/// </summary>
internal sealed class GeometryProcessor : IGeometryProcessor
{
    private readonly int _geoJsonTextPrecision;

    public GeometryProcessor(int geoJsonTextPrecision = FeatureQueryEncoding.GeometryTextPrecision)
    {
        _geoJsonTextPrecision = Math.Clamp(geoJsonTextPrecision, 0, 15);
    }

    public string GetGeometrySelectExpression(CoreGeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = DatumTransformSql.BuildTransformExpression(baseGeometry, query.OutputSrid.Value, query.OutputDatumTransformation);
        }

        // Default to 2D OGC WKB (ST_AsBinary) — byte-identical to the historical read path. When the
        // canonical query asks to preserve Z/M, read extended WKB (ST_AsEWKB) so the higher ordinates
        // survive to the output formatter; ST_AsBinary silently drops them. The WKB consumers (NTS
        // WKBReader and the GeoServices fast-point parser) read EWKB transparently, including the SRID
        // prefix, so the switch is safe for the geometries that actually carry Z/M.
        var geometryEncoder = query.IncludeZ || query.IncludeM ? "ST_AsEWKB" : "ST_AsBinary";
        return $"{geometryEncoder}({baseGeometry}) AS {FeatureQueryEncoding.GeometryColumn}";
    }

    public string GetGeometryGmlExpression(CoreGeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = DatumTransformSql.BuildTransformExpression(baseGeometry, query.OutputSrid.Value, query.OutputDatumTransformation);
        }

        // GML 3.2 (ISO 19136) requires gml:id on every GML object, geometries included;
        // PostGIS emits it via ST_AsGML's 6th argument. Derive an NCName-safe id that is
        // unique within a GetFeature document from the feature identity columns, which are
        // always selected alongside this expression (see FeatureQueryBuilder select clauses).
        var gmlIdExpression =
            $"'geom.' || {DatabaseSchema.LayerIdColumn}::text || '.' || {DatabaseSchema.ObjectIdColumn}::text";

        // GML 3.2 surface patches follow the ISO 19107 right-hand rule (exterior ring CCW, holes
        // CW). Stored data is frequently CW-exterior (Esri applyEdits, shapefile imports), so
        // enforce the orientation to match the GeoJSON path; ST_ForcePolygonCCW is a no-op for
        // non-polygonal geometry. The geometric surface orientation is preserved through the GML
        // axis-flip option: option 16 relabels axes into srsName (lat/lon) order without reversing
        // the ring, so the CCW-exterior winding still holds (matches GeoServer) (#2745).
        return $"ST_AsGML(3, ST_ForcePolygonCCW({baseGeometry}), {FeatureQueryEncoding.GeometryTextPrecision}, {GetGmlOptions(query)}, NULL, {gmlIdExpression})";
    }

    public string GetGeometryGeoJsonExpression(CoreGeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = DatumTransformSql.BuildTransformExpression(baseGeometry, query.OutputSrid.Value, query.OutputDatumTransformation);
        }

        // RFC 7946 §3.1.6 requires right-hand-rule winding (exterior CCW, holes CW).
        // Stored data is frequently CW-exterior (Esri applyEdits, shapefile imports), and
        // the preformatted fast paths pass this text through verbatim, so enforce the
        // orientation here to match the NTS slow path (which already enforces RFC 7946).
        // ST_ForcePolygonCCW returns non-polygonal geometries unchanged.
        return $"ST_AsGeoJSON(ST_ForcePolygonCCW({baseGeometry}), {_geoJsonTextPrecision}, 0)";
    }

    public string GetGeometryKmlExpression(CoreGeometryStorageType storageType, FeatureQuery query)
    {
        var baseGeometry = GetGeometryOperand(storageType, layerSrid: query.SpatialReferenceSrid);

        if (query.OutputSrid.HasValue &&
            (!query.SpatialReferenceSrid.HasValue || query.OutputSrid.Value != query.SpatialReferenceSrid.Value))
        {
            baseGeometry = DatumTransformSql.BuildTransformExpression(baseGeometry, query.OutputSrid.Value, query.OutputDatumTransformation);
        }

        // KML polygons use right-hand-rule winding (exterior CCW, holes CW); normalize CW-exterior
        // stored data to match the GeoJSON/GML paths. ST_ForcePolygonCCW passes non-polygonal
        // geometry through unchanged (#2745).
        return $"ST_AsKML(ST_ForcePolygonCCW({baseGeometry}), {FeatureQueryEncoding.GeometryTextPrecision})";
    }

    public string GetGeometryWriteExpression(CoreGeometryStorageType storageType, string parameterName, int? layerSrid)
    {
        return storageType switch
        {
            CoreGeometryStorageType.Geometry => BuildGeometryWriteExpression(parameterName, layerSrid),
            CoreGeometryStorageType.Geography => BuildGeographyWriteExpression(parameterName, layerSrid),
            CoreGeometryStorageType.Bytea => parameterName,
            _ => parameterName
        };
    }

    public string GetGeometryOperand(CoreGeometryStorageType storageType, string? columnExpression = null, int? layerSrid = null)
    {
        var column = columnExpression ?? FeatureQueryEncoding.GeometryColumn;
        var operand = storageType switch
        {
            CoreGeometryStorageType.Geometry => column,
            CoreGeometryStorageType.Geography => $"{column}::geometry",
            CoreGeometryStorageType.Bytea => $"ST_GeomFromEWKB({column})",
            _ => column
        };

        if (storageType == CoreGeometryStorageType.Bytea && layerSrid.HasValue)
        {
            operand = $"ST_SetSRID({operand}, {layerSrid.Value})";
        }

        return operand;
    }

    public string BuildSpatialFilterGeometryExpression(SpatialFilter filter, FeatureQuery query, ref int paramIndex)
    {
        var parameterIndex = paramIndex++;
        var baseGeometry = $"ST_GeomFromEWKB(${parameterIndex})";
        var geometryExpression = baseGeometry;

        if (filter.Srid.HasValue)
        {
            geometryExpression =
                $"ST_SetSRID({baseGeometry}, COALESCE(NULLIF(ST_SRID({baseGeometry}), 0), {filter.Srid.Value}))";
        }

        if (filter.Srid.HasValue && query.SpatialReferenceSrid.HasValue &&
            filter.Srid.Value != query.SpatialReferenceSrid.Value)
        {
            geometryExpression = $"ST_Transform({geometryExpression}, {query.SpatialReferenceSrid.Value})";
        }

        return geometryExpression;
    }

    public double ConvertDistanceToMeters(double distance, DistanceUnit unit) =>
        DistanceConversions.ToMeters(distance, unit);

    /// <summary>
    /// Gets the geometry operand for geography operations (WGS84)
    /// </summary>
    public string GetGeographyOperand(CoreGeometryStorageType storageType, int? layerSrid)
    {
        var geometryOperand = storageType switch
        {
            CoreGeometryStorageType.Geography => FeatureQueryEncoding.GeometryColumn,
            CoreGeometryStorageType.Geometry => FeatureQueryEncoding.GeometryColumn,
            CoreGeometryStorageType.Bytea => $"ST_GeomFromEWKB({FeatureQueryEncoding.GeometryColumn})",
            _ => FeatureQueryEncoding.GeometryColumn
        };

        if (storageType == CoreGeometryStorageType.Geography)
        {
            return geometryOperand;
        }

        if (storageType == CoreGeometryStorageType.Bytea && layerSrid.HasValue)
        {
            geometryOperand = $"ST_SetSRID({geometryOperand}, {layerSrid.Value})";
        }

        var wgs84Srid = SpatialReference.WGS84.Wkid;
        if (layerSrid.HasValue && layerSrid.Value != wgs84Srid)
        {
            geometryOperand = $"ST_Transform({geometryOperand}, {wgs84Srid})";
        }

        return $"{geometryOperand}::geography";
    }

    private static string BuildGeometryWriteExpression(string parameterName, int? layerSrid)
    {
        var baseGeometry = $"ST_GeomFromEWKB({parameterName})";
        if (!layerSrid.HasValue)
        {
            return baseGeometry;
        }

        return $"ST_SetSRID({baseGeometry}, COALESCE(NULLIF(ST_SRID({baseGeometry}), 0), {layerSrid.Value}))";
    }

    private static string BuildGeographyWriteExpression(string parameterName, int? layerSrid)
    {
        var targetSrid = SpatialReference.WGS84.Wkid;
        var baseGeometry = $"ST_GeomFromEWKB({parameterName})";
        var assumedSrid = layerSrid ?? targetSrid;
        return $"ST_SetSRID({baseGeometry}, COALESCE(NULLIF(ST_SRID({baseGeometry}), 0), {assumedSrid}))::geography";
    }

    private static int GetGmlOptions(FeatureQuery query)
    {
        var axisOrder = query.OutputAxisOrder;
        if (!axisOrder.HasValue)
        {
            var outputSrid = query.OutputSrid ?? query.SpatialReferenceSrid;
            if (outputSrid.HasValue && SpatialReference.Create(outputSrid.Value).IsGeographic)
            {
                axisOrder = AxisOrder.NorthEast;
            }
        }

        const int longSrsNameOption = 1;
        const int axisFlipOption = 16;
        return axisOrder == AxisOrder.NorthEast
            ? longSrsNameOption | axisFlipOption
            : longSrsNameOption;
    }
}
