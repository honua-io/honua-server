// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Abstraction over the field-schema lookup that the filter normalizer / translator
/// needs. Wraps a Metadata v2 <see cref="MetadataV2Resource"/> without leaking
/// graph details into the recursive normalization logic.
/// </summary>
/// <remarks>
/// The struct is constructed via <see cref="From(MetadataV2Resource)"/>.
/// <see cref="TryGetFieldType"/> returns the field's canonical <see cref="MetadataV2FieldType"/>;
/// the implementation handles the
/// well-known synthetic fields (<c>objectid</c>, <c>layerid</c>, <c>geometry</c>,
/// <c>shape</c>, the audit-timestamp columns <c>created_at</c>/<c>updated_at</c>, and
/// their reserved canonical aliases <c>created</c>/<c>updated</c>) before consulting
/// the schema.
/// </remarks>
public readonly struct FilterFieldSchema
{
    private readonly MetadataV2Resource? _resource;

    private FilterFieldSchema(MetadataV2Resource? resource)
    {
        _resource = resource;
    }

    /// <summary>
    /// Wraps a Metadata v2 resource for the filter pipeline. Reads the field set from
    /// <see cref="MetadataV2Resource.SchemaFields"/>.
    /// </summary>
    public static FilterFieldSchema From(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new FilterFieldSchema(resource);
    }

    /// <summary>
    /// True when this schema does not wrap any underlying type (default-constructed).
    /// </summary>
    public bool IsEmpty => _resource is null;

    /// <summary>
    /// Returns the canonical <see cref="MetadataV2FieldType"/> for the named field, applying the
    /// synthetic-field overrides (objectid → BigInteger, geometry/shape → Geometry, etc.).
    /// </summary>
    public bool TryGetFieldType(string fieldName, out MetadataV2FieldType fieldType)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        if (fieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            fieldType = MetadataV2FieldType.BigInteger;
            return true;
        }
        if (fieldName.Equals("layerid", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("layer_id", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = MetadataV2FieldType.Integer;
            return true;
        }
        if (fieldName.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("shape", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = MetadataV2FieldType.Geometry;
            return true;
        }
        // Physical audit-timestamp columns plus the reserved canonical aliases
        // ("created"/"updated") that resolve to them. The aliases are how server-side
        // callers (e.g. the OData $deltatoken delta predicate) reference the system
        // updated/created columns without colliding with the #1415 contract that a
        // user-supplied filter on the literal "created_at"/"updated_at" name resolves
        // through the layer schema.
        if (fieldName.Equals("created_at", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("updated_at", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("created", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("updated", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = MetadataV2FieldType.DateTime;
            return true;
        }

        if (_resource is not null)
        {
            foreach (var schemaField in _resource.SchemaFields)
            {
                if (string.Equals(schemaField.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    fieldType = NormalizeFieldType(schemaField.Type);
                    return true;
                }
            }
        }

        fieldType = MetadataV2FieldType.String;
        return false;
    }

    /// <summary>
    /// Normalizes a schema field type for the filter pipeline: collapses the spatial
    /// <see cref="MetadataV2FieldType.Geography"/> alias to <see cref="MetadataV2FieldType.Geometry"/>
    /// and maps <see cref="MetadataV2FieldType.Unknown"/> to <see cref="MetadataV2FieldType.String"/>.
    /// </summary>
    private static MetadataV2FieldType NormalizeFieldType(MetadataV2FieldType type) => type switch
    {
        MetadataV2FieldType.Geography => MetadataV2FieldType.Geometry,
        MetadataV2FieldType.Unknown => MetadataV2FieldType.String,
        _ => type,
    };
}
