// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Definition of a field in a layer's attribute schema
/// </summary>
/// <param name="Name">Field name (database column name)</param>
/// <param name="Type">Data type of the field</param>
/// <param name="Length">Maximum length for string fields, null for other types</param>
/// <param name="Nullable">Whether the field accepts null values</param>
/// <param name="DefaultValue">Default value for the field (optional)</param>
/// <param name="Description">Human-readable description of the field (optional)</param>
/// <param name="Domain">Optional schema-defined domain for the field.</param>
/// <param name="IsHidden">Whether public protocol output should omit the field.</param>
public record FieldDefinition(
    string Name,
    MetadataV2FieldType Type,
    int? Length = null,
    bool Nullable = true,
    object? DefaultValue = null,
    string? Description = null,
    FieldDomainDefinition? Domain = null,
    bool IsHidden = false)
{
    /// <summary>
    /// Display name for the field (uses Name if not specified)
    /// </summary>
    public string DisplayName => Description ?? Name;

    /// <summary>
    /// Whether this field is a geometry field
    /// </summary>
    public bool IsGeometry => Type == MetadataV2FieldType.Geometry;

    /// <summary>
    /// Whether this field is visible in public protocol metadata and feature output.
    /// </summary>
    public bool IsVisible => !IsHidden;

    /// <summary>
    /// Type name suitable for GeoServices REST API responses
    /// </summary>
    public string GeoServicesType => Type switch
    {
        MetadataV2FieldType.String => "esriFieldTypeString",
        MetadataV2FieldType.Integer => "esriFieldTypeInteger",
        MetadataV2FieldType.BigInteger => "esriFieldTypeInteger64",
        MetadataV2FieldType.Double => "esriFieldTypeDouble",
        MetadataV2FieldType.Float => "esriFieldTypeSingle",
        MetadataV2FieldType.Boolean => "esriFieldTypeSmallInteger", // Boolean as 0/1
        MetadataV2FieldType.DateTime => "esriFieldTypeDate",
        MetadataV2FieldType.Date => "esriFieldTypeDate",
        MetadataV2FieldType.Time => "esriFieldTypeString", // Time as formatted string
        MetadataV2FieldType.Geometry => "esriFieldTypeGeometry",
        MetadataV2FieldType.Json => "esriFieldTypeString", // JSON as string
        MetadataV2FieldType.Binary => "esriFieldTypeBlob",
        MetadataV2FieldType.Uuid => "esriFieldTypeGUID",
        _ => "esriFieldTypeString"
    };

    /// <summary>
    /// SQL type name for database schema generation
    /// </summary>
    public string SqlType => Type switch
    {
        MetadataV2FieldType.String when Length.HasValue => $"VARCHAR({Length})",
        MetadataV2FieldType.String => "TEXT",
        MetadataV2FieldType.Integer => "INTEGER",
        MetadataV2FieldType.BigInteger => "BIGINT",
        MetadataV2FieldType.Double => "DOUBLE PRECISION",
        MetadataV2FieldType.Float => "REAL",
        MetadataV2FieldType.Boolean => "BOOLEAN",
        MetadataV2FieldType.DateTime => "TIMESTAMP WITH TIME ZONE",
        MetadataV2FieldType.Date => "DATE",
        MetadataV2FieldType.Time => "TIME",
        MetadataV2FieldType.Geometry => "GEOMETRY", // PostGIS type
        MetadataV2FieldType.Json => "JSONB",
        MetadataV2FieldType.Binary => "BYTEA",
        MetadataV2FieldType.Uuid => "UUID",
        _ => "TEXT"
    };

    /// <summary>
    /// Validates field definition for common issues
    /// </summary>
    /// <returns>Validation error message if invalid, null if valid</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Field name cannot be empty";

        if (Name.Length > 64)
            return "Field name cannot exceed 64 characters";

        if (Type == MetadataV2FieldType.String && Length.HasValue && Length.Value <= 0)
            return "String field length must be positive";

        if (Type == MetadataV2FieldType.String && Length.HasValue && Length.Value > 8000)
            return "String field length cannot exceed 8000 characters";

        return null; // Valid
    }
}
