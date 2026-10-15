// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Shared publisher-hidden field rules (<see cref="MetadataV2Field.Hidden"/>) for public read
/// surfaces. A declared hidden field is never emitted, searched, filtered, classified or
/// aggregated by a protocol read path; undeclared runtime attributes are unaffected. Field
/// masks (request-scoped field-level security) are a separate layer and are not applied here.
/// </summary>
public static class MetadataV2FieldVisibilityExtensions
{
    /// <summary>
    /// Returns the declared schema fields that are not publisher-hidden.
    /// </summary>
    public static IEnumerable<MetadataV2Field> VisibleSchemaFields(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SchemaFields.Where(static field => !field.Hidden);
    }

    /// <summary>
    /// Resolves a declared, non-hidden schema field by name (case-insensitive). A hidden field
    /// resolves to <see langword="null"/>, exactly like an unknown field, so callers cannot
    /// distinguish the two.
    /// </summary>
    public static MetadataV2Field? FindVisibleSchemaField(this MetadataV2Resource resource, string? fieldName)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        return resource.SchemaFields.FirstOrDefault(field =>
            !field.Hidden && string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the names of the declared publisher-hidden fields (case-insensitive set).
    /// </summary>
    public static IReadOnlySet<string> HiddenSchemaFieldNames(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SchemaFields
            .Where(static field => field.Hidden)
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="fieldName"/> names a declared publisher-hidden field.
    /// </summary>
    public static bool IsHiddenSchemaField(this MetadataV2Resource resource, string? fieldName)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return !string.IsNullOrWhiteSpace(fieldName) && resource.SchemaFields.Any(field =>
            field.Hidden && string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns a copy of the resource whose <see cref="MetadataV2Resource.SchemaFields"/> omit
    /// publisher-hidden fields, for resolving caller-supplied property names (filters, search
    /// fields) against the public schema. Returns the same instance when nothing is hidden.
    /// </summary>
    public static MetadataV2Resource WithoutHiddenSchemaFields(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.SchemaFields.Any(static field => field.Hidden)
            ? resource with { SchemaFields = resource.SchemaFields.Where(static field => !field.Hidden).ToArray() }
            : resource;
    }
}
