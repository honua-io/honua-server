// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Queries.Filters;

public readonly struct FilterTranslationContext
{
    public readonly record struct ContextField(string Name, MetadataV2FieldType Type, bool IsGeometry, bool IsPrimaryKey);

    private FilterTranslationContext(
        IReadOnlyList<ContextField> fields,
        string? primaryKeyName,
        string? geometryColumnName,
        int wkid,
        bool isGeographic,
        GeometryType geometryType,
        string resourceName)
    {
        Fields = fields;
        PrimaryKeyName = primaryKeyName;
        GeometryColumnName = geometryColumnName;
        Wkid = wkid;
        IsGeographic = isGeographic;
        GeometryType = geometryType;
        ResourceName = resourceName;
    }

    public IReadOnlyList<ContextField> Fields { get; }

    public string? PrimaryKeyName { get; }

    public string? GeometryColumnName { get; }

    public int Wkid { get; }

    public bool IsGeographic { get; }

    public GeometryType GeometryType { get; }

    public string ResourceName { get; }

    public static FilterTranslationContext FromResource(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var primaryKey = resource.FindPrimaryIdField();
        var geometryField = resource.FindPrimaryGeometryField();
        var fields = new ContextField[resource.SchemaFields.Count];
        for (var i = 0; i < resource.SchemaFields.Count; i++)
        {
            var f = resource.SchemaFields[i];
            var isPk = primaryKey is not null && string.Equals(f.Name, primaryKey.Name, StringComparison.OrdinalIgnoreCase);
            var isGeom = f.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography ||
                (geometryField is not null && string.Equals(f.Name, geometryField.Name, StringComparison.OrdinalIgnoreCase));
            fields[i] = new ContextField(f.Name, NormalizeFieldType(f.Type), isGeom, isPk);
        }

        var srid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var isGeographic = resource.Spatial?.SpatialReference?.IsGeographic ?? false;

        return new FilterTranslationContext(
            fields,
            primaryKey?.Name,
            geometryField?.Name,
            srid,
            isGeographic,
            MapGeometryType(resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None),
            resource.Metadata.Name);
    }

    public static FilterTranslationContext FromColumns(
        IReadOnlyList<ContextField> fields,
        string? primaryKeyName,
        string? geometryColumnName,
        int wkid,
        GeometryType geometryType,
        string resourceName,
        bool isGeographic = false)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(resourceName);
        return new FilterTranslationContext(
            fields,
            primaryKeyName,
            geometryColumnName,
            wkid,
            isGeographic,
            geometryType,
            resourceName);
    }

    private static MetadataV2FieldType NormalizeFieldType(MetadataV2FieldType type) => type switch
    {
        MetadataV2FieldType.Geography => MetadataV2FieldType.Geometry,
        MetadataV2FieldType.Unknown => MetadataV2FieldType.String,
        _ => type,
    };

    private static GeometryType MapGeometryType(MetadataV2GeometryType type) => type switch
    {
        MetadataV2GeometryType.None => GeometryType.None,
        MetadataV2GeometryType.Point => GeometryType.Point,
        MetadataV2GeometryType.MultiPoint => GeometryType.MultiPoint,
        MetadataV2GeometryType.LineString => GeometryType.LineString,
        MetadataV2GeometryType.MultiLineString => GeometryType.MultiLineString,
        MetadataV2GeometryType.Polygon => GeometryType.Polygon,
        MetadataV2GeometryType.MultiPolygon => GeometryType.MultiPolygon,
        MetadataV2GeometryType.GeometryCollection => GeometryType.GeometryCollection,
        _ => GeometryType.None,
    };

    public ContextField? TryGetField(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        for (var i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return Fields[i];
            }
        }
        return null;
    }
}
