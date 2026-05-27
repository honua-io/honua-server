// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Provider-neutral schema context consumed by <see cref="ISqlFilterTranslator"/>
/// implementations. Lets translator implementations consume Metadata v2 resources
/// without duplicating recursive translation logic.
/// </summary>
/// <remarks>
/// The context exposes only the surface the translators actually need: field
/// metadata (name / type / geometry flag / primary-key flag), the geometry column
/// name and SRID, and the resource name used in error messages. SRID resolution
/// falls back to <see cref="SpatialReference.WGS84"/> when the resource does not
/// carry an explicit SRID.
/// </remarks>
public readonly struct FilterTranslationContext
{
    /// <summary>
    /// A single field exposed by <see cref="FilterTranslationContext.Fields"/>.
    /// </summary>
    /// <param name="Name">Stable source field name.</param>
    /// <param name="Type">Canonical <see cref="FieldType"/> (v1 enum).</param>
    /// <param name="IsGeometry">True when the field carries the resource's geometry column.</param>
    /// <param name="IsPrimaryKey">True when the field is the resource's primary key.</param>
    public readonly record struct ContextField(string Name, FieldType Type, bool IsGeometry, bool IsPrimaryKey);

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

    /// <summary>Ordered field set used for property/field resolution.</summary>
    public IReadOnlyList<ContextField> Fields { get; }

    /// <summary>Name of the primary-key field, or null when the resource lacks one.</summary>
    public string? PrimaryKeyName { get; }

    /// <summary>Name of the primary geometry column, or null for non-spatial resources.</summary>
    public string? GeometryColumnName { get; }

    /// <summary>SRID of the resource's coordinate reference system.</summary>
    public int Wkid { get; }

    /// <summary>True when the resource's SRS uses geographic (spherical) coordinates.</summary>
    public bool IsGeographic { get; }

    /// <summary>Canonical geometry type for the resource (or <see cref="GeometryType.None"/>).</summary>
    public GeometryType GeometryType { get; }

    /// <summary>Display name of the resource, used in diagnostic messages.</summary>
    public string ResourceName { get; }

    /// <summary>
    /// Builds a <see cref="FilterTranslationContext"/> from a Metadata v2 resource.
    /// </summary>
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
            fields[i] = new ContextField(f.Name, MapFieldType(f.Type), isGeom, isPk);
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

    /// <summary>Maps a Metadata v2 field type to the v1 <see cref="FieldType"/> used by the translators.</summary>
    private static FieldType MapFieldType(MetadataV2FieldType type) => type switch
    {
        MetadataV2FieldType.String => FieldType.String,
        MetadataV2FieldType.Integer => FieldType.Integer,
        MetadataV2FieldType.BigInteger => FieldType.BigInteger,
        MetadataV2FieldType.Double => FieldType.Double,
        MetadataV2FieldType.Float => FieldType.Float,
        MetadataV2FieldType.Boolean => FieldType.Boolean,
        MetadataV2FieldType.DateTime => FieldType.DateTime,
        MetadataV2FieldType.Date => FieldType.Date,
        MetadataV2FieldType.Time => FieldType.Time,
        MetadataV2FieldType.Json => FieldType.Json,
        MetadataV2FieldType.Binary => FieldType.Binary,
        MetadataV2FieldType.Uuid => FieldType.Uuid,
        MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography => FieldType.Geometry,
        _ => FieldType.String,
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

    /// <summary>
    /// Tries to locate a field by name (case-insensitive). Returns null when missing.
    /// </summary>
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
