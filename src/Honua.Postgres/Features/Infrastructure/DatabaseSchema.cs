// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Shared.Models;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Centralized database schema constants for PostgreSQL feature store operations.
/// Contains table names, column names, and common field identifiers used throughout the data layer.
/// </summary>
internal static class DatabaseSchema
{
    #region Table Names

    /// <summary>
    /// Main features table name.
    /// </summary>
    public const string FeaturesTable = "features";

    /// <summary>
    /// Attachments table name for feature file attachments.
    /// </summary>
    public const string AttachmentsTable = "attachments";

    /// <summary>
    /// Relationships table name for feature relationships.
    /// </summary>
    public const string RelationshipsTable = "relationships";

    /// <summary>
    /// Layer definitions table name for metadata catalog.
    /// </summary>
    public const string LayersTable = "layers";

    /// <summary>
    /// Service definitions table name for service catalog.
    /// </summary>
    public const string ServicesTable = "services";

    /// <summary>
    /// Secure connections registry table name.
    /// </summary>
    public const string SecureConnectionsTable = "secure_connections";

    #endregion

    #region Standard Column Names

    /// <summary>
    /// Primary key column name for features (objectid).
    /// Standard across all feature-related operations.
    /// </summary>
    public const string ObjectIdColumn = FieldNames.ObjectId;

    /// <summary>
    /// Alternative object ID column name (object_id) for compatibility.
    /// </summary>
    public const string ObjectIdColumnAlt = "object_id";

    /// <summary>
    /// Generic ID column name for non-feature tables.
    /// </summary>
    public const string IdColumn = "id";

    /// <summary>
    /// Geometry column name for spatial data.
    /// </summary>
    public const string GeometryColumn = "geometry";

    /// <summary>
    /// Attributes column name for JSON feature attributes.
    /// </summary>
    public const string AttributesColumn = "attributes";

    /// <summary>
    /// Layer ID column name for associating features with layers.
    /// </summary>
    public const string LayerIdColumn = "layer_id";

    /// <summary>
    /// Layer ID column alternative name (layerid) for compatibility.
    /// </summary>
    public const string LayerIdColumnAlt = "layerid";

    /// <summary>
    /// Service ID column name for associating layers with services.
    /// </summary>
    public const string ServiceIdColumn = "service_id";

    /// <summary>
    /// Created timestamp column name.
    /// </summary>
    public const string CreatedColumn = "created";

    /// <summary>
    /// Updated timestamp column name.
    /// </summary>
    public const string UpdatedColumn = "updated";

    /// <summary>
    /// Name column for descriptive names.
    /// </summary>
    public const string NameColumn = "name";

    /// <summary>
    /// Title column for display titles.
    /// </summary>
    public const string TitleColumn = "title";

    /// <summary>
    /// Description column for detailed descriptions.
    /// </summary>
    public const string DescriptionColumn = "description";

    #endregion

    #region Field Mapping Helpers

    /// <summary>
    /// Maps common field aliases to their canonical column names.
    /// </summary>
    private static readonly FrozenDictionary<string, string> _fieldMappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ObjectIdColumn] = ObjectIdColumn,
            [ObjectIdColumnAlt] = ObjectIdColumn,
            [IdColumn] = ObjectIdColumn,
            [LayerIdColumn] = LayerIdColumn,
            [LayerIdColumnAlt] = LayerIdColumn,
            [GeometryColumn] = GeometryColumn
        }
        .ToFrozenDictionary();

    /// <summary>
    /// Maps a field name to its canonical column name.
    /// Handles common aliases and ensures consistent field naming.
    /// </summary>
    /// <param name="fieldName">Input field name (may be an alias)</param>
    /// <returns>Canonical column name</returns>
    public static string MapFieldName(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName))
            return fieldName;

        return _fieldMappings.TryGetValue(fieldName, out var mappedName) ? mappedName : fieldName;
    }

    /// <summary>
    /// Determines if a field name refers to a system column that cannot be used in custom attributes.
    /// </summary>
    /// <param name="fieldName">Field name to check</param>
    /// <returns>True if the field is a protected system column, false otherwise</returns>
    public static bool IsSystemColumn(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName))
            return false;

        var normalizedField = fieldName.ToLowerInvariant();
        return normalizedField switch
        {
            ObjectIdColumn or ObjectIdColumnAlt or IdColumn => true,
            GeometryColumn => true,
            LayerIdColumn or LayerIdColumnAlt => true,
            AttributesColumn => true,
            CreatedColumn or UpdatedColumn => true,
            _ => false
        };
    }

    /// <summary>
    /// Determines if a field can be used in JSON path expressions (attributes->>'field').
    /// </summary>
    /// <param name="fieldName">Field name to check</param>
    /// <returns>True if the field can use JSON path syntax, false if it's a direct column</returns>
    public static bool CanUseJsonPath(string fieldName)
    {
        return !IsSystemColumn(fieldName);
    }

    #endregion

    #region Query Building Helpers

    /// <summary>
    /// Gets the fully qualified table name with optional schema prefix.
    /// </summary>
    /// <param name="tableName">Base table name</param>
    /// <param name="schemaName">Optional schema name</param>
    /// <returns>Fully qualified table name</returns>
    public static string GetQualifiedTableName(string tableName, string? schemaName = null)
    {
        return string.IsNullOrEmpty(schemaName) ? $"\"{tableName}\"" : $"\"{schemaName}\".\"{tableName}\"";
    }

    /// <summary>
    /// Gets the qualified features table name with optional schema.
    /// </summary>
    /// <param name="schemaName">Optional schema name</param>
    /// <returns>Qualified features table name</returns>
    public static string GetFeaturesTableName(string? schemaName = null)
    {
        return GetQualifiedTableName(FeaturesTable, schemaName);
    }

    /// <summary>
    /// Builds a JSON path expression for attribute access.
    /// </summary>
    /// <param name="attributeName">Name of the attribute to access</param>
    /// <param name="columnName">Base column name (default: attributes)</param>
    /// <returns>PostgreSQL JSON path expression like "attributes->>'field_name'"</returns>
    public static string BuildJsonPath(string attributeName, string columnName = AttributesColumn)
    {
        // Escape single quotes in attribute names
        var escapedAttributeName = attributeName.Replace("'", "''");
        return $"{columnName}->>'{escapedAttributeName}'";
    }

    /// <summary>
    /// Builds a JSON path expression using a parameter placeholder for the attribute name.
    /// </summary>
    /// <param name="parameterIndex">The SQL parameter index for the attribute name</param>
    /// <param name="columnName">Base column name (default: attributes)</param>
    /// <returns>PostgreSQL JSON path expression like "attributes->> $2"</returns>
    public static string BuildJsonPathParameter(int parameterIndex, string columnName = AttributesColumn)
    {
        return $"{columnName}->> ${parameterIndex}";
    }

    #endregion
}
