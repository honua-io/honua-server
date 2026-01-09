// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Base field definition properties shared across all protocols and APIs.
/// Consolidates common field metadata to eliminate duplication between GeoServices, OGC, and import models.
/// </summary>
public abstract record FieldDefinitionBase
{
    /// <summary>
    /// Field name (database column name)
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Field type in protocol-specific format (e.g., "esriFieldTypeString", "string")
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Human-readable alias or display name for the field
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// Maximum length for string fields, null for other types
    /// </summary>
    public int? Length { get; init; }

    /// <summary>
    /// Whether the field accepts null values
    /// </summary>
    public bool Nullable { get; init; } = true;

    /// <summary>
    /// Default value for the field (optional)
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// Gets the effective display name (Alias ?? Name)
    /// </summary>
    public string DisplayName => Alias ?? Name;

    /// <summary>
    /// Validates field definition for common issues
    /// </summary>
    /// <returns>Validation error message if invalid, null if valid</returns>
    public virtual string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Field name cannot be empty";

        if (Name.Length > 64)
            return "Field name cannot exceed 64 characters";

        // Length validation for string-like types (protocol-specific validation in derived classes)
        if (IsStringType() && Length.HasValue && Length.Value <= 0)
            return "String field length must be positive";

        if (IsStringType() && Length.HasValue && Length.Value > 8000)
            return "String field length cannot exceed 8000 characters";

        return null; // Valid
    }

    /// <summary>
    /// Determines if this field represents a string type based on the Type value.
    /// Override in derived classes for protocol-specific string type detection.
    /// </summary>
    /// <returns>True if this is a string-based field type</returns>
    protected virtual bool IsStringType()
    {
        var typeUpper = Type.ToUpperInvariant();
        return typeUpper.Contains("STRING") || typeUpper.Contains("TEXT") || typeUpper.Contains("VARCHAR");
    }
}
