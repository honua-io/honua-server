// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
public record FieldDefinition(
    string Name,
    FieldType Type,
    int? Length = null,
    bool Nullable = true,
    object? DefaultValue = null,
    string? Description = null)
{
    /// <summary>
    /// Display name for the field (uses Name if not specified)
    /// </summary>
    public string DisplayName => Description ?? Name;

    /// <summary>
    /// Whether this field is a geometry field
    /// </summary>
    public bool IsGeometry => Type == FieldType.Geometry;

    /// <summary>
    /// Type name suitable for GeoServices REST API responses
    /// </summary>
    public string GeoServicesType => Type switch
    {
        FieldType.String => "esriFieldTypeString",
        FieldType.Integer => "esriFieldTypeInteger",
        FieldType.BigInteger => "esriFieldTypeInteger",
        FieldType.Double => "esriFieldTypeDouble",
        FieldType.Float => "esriFieldTypeSingle",
        FieldType.Boolean => "esriFieldTypeSmallInteger", // Boolean as 0/1
        FieldType.DateTime => "esriFieldTypeDate",
        FieldType.Date => "esriFieldTypeDate",
        FieldType.Time => "esriFieldTypeString", // Time as formatted string
        FieldType.Geometry => "esriFieldTypeGeometry",
        FieldType.Json => "esriFieldTypeString", // JSON as string
        FieldType.Binary => "esriFieldTypeBlob",
        FieldType.Uuid => "esriFieldTypeGUID",
        _ => "esriFieldTypeString"
    };

    /// <summary>
    /// SQL type name for database schema generation
    /// </summary>
    public string SqlType => Type switch
    {
        FieldType.String when Length.HasValue => $"VARCHAR({Length})",
        FieldType.String => "TEXT",
        FieldType.Integer => "INTEGER",
        FieldType.BigInteger => "BIGINT",
        FieldType.Double => "DOUBLE PRECISION",
        FieldType.Float => "REAL",
        FieldType.Boolean => "BOOLEAN",
        FieldType.DateTime => "TIMESTAMP WITH TIME ZONE",
        FieldType.Date => "DATE",
        FieldType.Time => "TIME",
        FieldType.Geometry => "GEOMETRY", // PostGIS type
        FieldType.Json => "JSONB",
        FieldType.Binary => "BYTEA",
        FieldType.Uuid => "UUID",
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

        if (Type == FieldType.String && Length.HasValue && Length.Value <= 0)
            return "String field length must be positive";

        if (Type == FieldType.String && Length.HasValue && Length.Value > 8000)
            return "String field length cannot exceed 8000 characters";

        return null; // Valid
    }
}
