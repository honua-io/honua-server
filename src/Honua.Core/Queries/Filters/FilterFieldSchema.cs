// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Abstraction over the field-schema lookup that the filter normalizer / translator
/// needs. Lets the filter pipeline accept either a v1 <see cref="LayerDefinition"/> or
/// a Metadata v2 <see cref="MetadataV2Resource"/> without duplicating the recursive
/// normalization logic.
/// </summary>
/// <remarks>
/// The struct is constructed via <see cref="From(LayerDefinition)"/> or
/// <see cref="From(MetadataV2Resource)"/>. <see cref="TryGetFieldType"/> returns the
/// field's canonical <see cref="FieldType"/>; the implementation handles the
/// well-known synthetic fields (<c>objectid</c>, <c>layerid</c>, <c>geometry</c>,
/// <c>shape</c>, <c>created_at</c>, <c>updated_at</c>) before consulting the schema.
/// </remarks>
public readonly struct FilterFieldSchema
{
    private readonly LayerDefinition? _layer;
    private readonly MetadataV2Resource? _resource;

    private FilterFieldSchema(LayerDefinition? layer, MetadataV2Resource? resource)
    {
        _layer = layer;
        _resource = resource;
    }

    /// <summary>
    /// Wraps a v1 layer for the filter pipeline.
    /// </summary>
    public static FilterFieldSchema From(LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return new FilterFieldSchema(layer, null);
    }

    /// <summary>
    /// Wraps a Metadata v2 resource for the filter pipeline. Reads the field set from
    /// <see cref="MetadataV2Resource.SchemaFields"/>.
    /// </summary>
    public static FilterFieldSchema From(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new FilterFieldSchema(null, resource);
    }

    /// <summary>
    /// True when this schema does not wrap any underlying type (default-constructed).
    /// </summary>
    public bool IsEmpty => _layer is null && _resource is null;

    /// <summary>
    /// Returns the canonical <see cref="FieldType"/> for the named field, applying the
    /// synthetic-field overrides (objectid → BigInteger, geometry/shape → Geometry, etc.).
    /// </summary>
    public bool TryGetFieldType(string fieldName, out FieldType fieldType)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        if (fieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            fieldType = FieldType.BigInteger;
            return true;
        }
        if (fieldName.Equals("layerid", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("layer_id", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = FieldType.Integer;
            return true;
        }
        if (fieldName.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("shape", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = FieldType.Geometry;
            return true;
        }
        if (fieldName.Equals("created_at", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("updated_at", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = FieldType.DateTime;
            return true;
        }

        if (_layer is not null)
        {
            var match = _layer.Fields.FirstOrDefault(f =>
                f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                fieldType = match.Type;
                return true;
            }
        }
        else if (_resource is not null)
        {
            foreach (var schemaField in _resource.SchemaFields)
            {
                if (string.Equals(schemaField.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    fieldType = ParseV2FieldType(schemaField.Type);
                    return true;
                }
            }
        }

        fieldType = FieldType.String;
        return false;
    }

    private static FieldType ParseV2FieldType(string? typeName)
    {
        var normalized = typeName?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "string" or "text" => FieldType.String,
            "integer" or "int32" or "int" => FieldType.Integer,
            "biginteger" or "int64" or "long" => FieldType.BigInteger,
            "double" => FieldType.Double,
            "float" or "real" or "single" => FieldType.Float,
            "boolean" or "bool" => FieldType.Boolean,
            "datetime" or "timestamp" => FieldType.DateTime,
            "date" => FieldType.Date,
            "time" => FieldType.Time,
            "json" or "object" => FieldType.Json,
            "binary" or "bytes" or "wkb" => FieldType.Binary,
            "uuid" or "guid" => FieldType.Uuid,
            "geometry" or "geography" => FieldType.Geometry,
            _ => FieldType.String,
        };
    }

    /// <summary>
    /// Returns the wrapped v1 layer when this schema came from a <see cref="LayerDefinition"/>;
    /// otherwise null. Use to call legacy v1-only translators that still need the full
    /// <see cref="LayerDefinition"/> shape.
    /// </summary>
    public LayerDefinition? V1Layer => _layer;

    /// <summary>
    /// Returns the wrapped V2 resource when this schema came from a
    /// <see cref="MetadataV2Resource"/>; otherwise null.
    /// </summary>
    public MetadataV2Resource? V2Resource => _resource;
}
