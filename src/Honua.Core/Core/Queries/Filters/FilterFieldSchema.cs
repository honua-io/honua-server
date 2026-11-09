// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Queries.Filters;

public readonly struct FilterFieldSchema
{
    private readonly MetadataV2Resource? _resource;

    private FilterFieldSchema(MetadataV2Resource? resource)
    {
        _resource = resource;
    }

    public static FilterFieldSchema From(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new FilterFieldSchema(resource);
    }

    public bool IsEmpty => _resource is null;

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
            var schemaField = _resource.SchemaFields.FirstOrDefault(field =>
                string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            if (schemaField is not null)
            {
                fieldType = NormalizeFieldType(schemaField.Type);
                return true;
            }
        }

        fieldType = MetadataV2FieldType.String;
        return false;
    }

    private static MetadataV2FieldType NormalizeFieldType(MetadataV2FieldType type) => type switch
    {
        MetadataV2FieldType.Geography => MetadataV2FieldType.Geometry,
        MetadataV2FieldType.Unknown => MetadataV2FieldType.String,
        _ => type,
    };
}
