// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

// A hidden field is omitted from every public read path, and a mask is a separate check.
public static class MetadataV2FieldVisibilityExtensions
{
    public static IEnumerable<MetadataV2Field> VisibleSchemaFields(this MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SchemaFields.Where(static field => !field.Hidden);
    }

    public static MetadataV2Field? FindVisibleSchemaField(this MetadataV2Object resource, string? fieldName)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        return resource.SchemaFields.FirstOrDefault(field =>
            !field.Hidden && string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlySet<string> HiddenSchemaFieldNames(this MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SchemaFields
            .Where(static field => field.Hidden)
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsHiddenSchemaField(this MetadataV2Object resource, string? fieldName)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return !string.IsNullOrWhiteSpace(fieldName) && resource.SchemaFields.Any(field =>
            field.Hidden && string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));
    }

    public static MetadataV2Object WithoutHiddenSchemaFields(this MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SchemaFields.Any(static field => field.Hidden)
            ? resource with { SchemaFields = resource.SchemaFields.Where(static field => !field.Hidden).ToArray() }
            : resource;
    }
}
