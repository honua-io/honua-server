// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Extension methods for converting between different field definition formats.
/// Eliminates duplication in field type mapping and conversion logic.
/// </summary>
public static class FieldDefinitionExtensions
{
    /// <summary>
    /// Converts a FieldDefinition from the Core catalog domain to a GeoServices-compatible field info
    /// </summary>
    /// <param name="field">The core field definition</param>
    /// <returns>A new FieldDefinitionBase with GeoServices type mapping</returns>
    public static T ToGeoServicesField<T>(this FieldDefinition field) where T : FieldDefinitionBase, new()
    {
        return new T
        {
            Name = field.Name,
            Type = field.GeoServicesType,
            Alias = field.DisplayName,
            Length = field.Length,
            Nullable = field.Nullable,
            DefaultValue = field.DefaultValue
        };
    }

    /// <summary>
    /// Converts an Esri field type to the corresponding FieldType enum
    /// </summary>
    /// <param name="esriType">The Esri field type string</param>
    /// <returns>The corresponding FieldType enum value</returns>
    public static FieldType ToFieldType(this string esriType)
    {
        return esriType.ToUpperInvariant() switch
        {
            "ESRIFIELDTYPEOID" => FieldType.BigInteger,
            "ESRIFIELDTYPEINTEGER" => FieldType.Integer,
            "ESRIFIELDTYPESMALLINTEGER" => FieldType.Integer,
            "ESRIFIELDTYPEDOUBLE" => FieldType.Double,
            "ESRIFIELDTYPESINGLE" => FieldType.Float,
            "ESRIFIELDTYPESTRING" => FieldType.String,
            "ESRIFIELDTYPEDATE" => FieldType.DateTime,
            "ESRIFIELDTYPEGUID" or "ESRIFIELDTYPEGLOBALID" => FieldType.Uuid,
            "ESRIFIELDTYPEBLOB" => FieldType.Binary,
            "ESRIFIELDTYPEXML" => FieldType.Json, // Map XML to JSON for simplicity
            "ESRIFIELDTYPEGEOMETRY" => FieldType.Geometry,
            _ => FieldType.String // Default fallback
        };
    }

    /// <summary>
    /// Converts a FieldType enum to the corresponding Esri field type string
    /// </summary>
    /// <param name="fieldType">The FieldType enum value</param>
    /// <returns>The corresponding Esri field type string</returns>
    public static string ToEsriType(this FieldType fieldType)
    {
        return fieldType switch
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
    }

    /// <summary>
    /// Converts an Esri field type to the corresponding PostgreSQL type
    /// </summary>
    /// <param name="esriType">The Esri field type string</param>
    /// <param name="length">The field length for variable-length types</param>
    /// <returns>The corresponding PostgreSQL type declaration</returns>
    public static string ToPostgresType(this string esriType, int? length = null)
    {
        return esriType.ToUpperInvariant() switch
        {
            "ESRIFIELDTYPEOID" => "INTEGER",
            "ESRIFIELDTYPEINTEGER" or "ESRIFIELDTYPESMALLINTEGER" => "INTEGER",
            "ESRIFIELDTYPEDOUBLE" or "ESRIFIELDTYPESINGLE" => "DOUBLE PRECISION",
            "ESRIFIELDTYPESTRING" => length.HasValue && length > 0 && length <= 8000
                ? $"VARCHAR({length})"
                : "TEXT",
            "ESRIFIELDTYPEDATE" => "TIMESTAMP WITH TIME ZONE",
            "ESRIFIELDTYPEGUID" or "ESRIFIELDTYPEGLOBALID" => "UUID",
            "ESRIFIELDTYPEBLOB" => "BYTEA",
            "ESRIFIELDTYPEXML" => "XML",
            "ESRIFIELDTYPEGEOMETRY" => "GEOMETRY",
            _ => "TEXT"
        };
    }

    /// <summary>
    /// Converts a FieldType enum to the corresponding PostgreSQL type
    /// </summary>
    /// <param name="fieldType">The FieldType enum value</param>
    /// <param name="length">The field length for variable-length types</param>
    /// <returns>The corresponding PostgreSQL type declaration</returns>
    public static string ToPostgresType(this FieldType fieldType, int? length = null)
    {
        return fieldType switch
        {
            FieldType.String when length.HasValue => $"VARCHAR({length})",
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
    }

    /// <summary>
    /// Creates a FieldDefinition from an EsriFieldInfo-like object
    /// </summary>
    /// <param name="esriField">The Esri field definition</param>
    /// <returns>A new FieldDefinition with converted types</returns>
    public static FieldDefinition ToFieldDefinition(this FieldDefinitionBase esriField)
    {
        return new FieldDefinition(
            Name: esriField.Name,
            Type: esriField.Type.ToFieldType(),
            Length: esriField.Length,
            Nullable: esriField.Nullable,
            DefaultValue: esriField.DefaultValue,
            Description: esriField.Alias);
    }

    /// <summary>
    /// Validates that a field name is safe for database usage
    /// </summary>
    /// <param name="fieldName">The field name to validate</param>
    /// <returns>A sanitized field name safe for database usage</returns>
    public static string SanitizeFieldName(this string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return "unnamed_field";

        // Replace problematic characters with underscores
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            fieldName, @"[^a-zA-Z0-9_]", "_");

        // Ensure it starts with a letter or underscore
        if (char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;

        // Limit length
        if (sanitized.Length > 63)
            sanitized = sanitized[..63];

        return sanitized.ToLowerInvariant();
    }
}
