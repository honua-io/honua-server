// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Postgres.Features.Infrastructure;

internal static class DatabaseSchema
{
    #region Table Names

    public const string FeaturesTable = "features";

    public const string AttachmentsTable = "attachments";

    public const string RelationshipsTable = "relationships";

    public const string LayersTable = "layers";

    public const string ServicesTable = "services";

    public const string SecureConnectionsTable = "secure_connections";

    #endregion

    #region Standard Column Names

    public const string ObjectIdColumn = FieldNames.ObjectId;

    public const string ObjectIdColumnAlt = "object_id";

    public const string IdColumn = "id";

    public const string GeometryColumn = "geometry";

    public const string AttributesColumn = "attributes";

    public const string LayerIdColumn = "layer_id";

    public const string LayerIdColumnAlt = "layerid";

    public const string ServiceIdColumn = "service_id";

    public const string CreatedColumn = "created";

    public const string UpdatedColumn = "updated";

    public const string NameColumn = "name";

    public const string TitleColumn = "title";

    public const string DescriptionColumn = "description";

    #endregion

    #region Field Mapping Helpers

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

    public static string MapFieldName(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName))
            return fieldName;

        return _fieldMappings.TryGetValue(fieldName, out var mappedName) ? mappedName : fieldName;
    }

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

    public static bool CanUseJsonPath(string fieldName)
    {
        return !IsSystemColumn(fieldName);
    }

    #endregion

    #region Query Building Helpers

    public static string GetQualifiedTableName(string tableName, string? schemaName = null)
    {
        if (!SchemaSearchPath.IsValidIdentifier(tableName))
        {
            throw new InvalidOperationException($"Invalid table name '{tableName}'.");
        }

        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return $"\"{tableName}\"";
        }

        return $"{SchemaSearchPath.ValidateAndQuote(schemaName)}.\"{tableName}\"";
    }

    public static string GetFeaturesTableName(string? schemaName = null)
    {
        return GetQualifiedTableName(FeaturesTable, schemaName);
    }

    public static string BuildJsonPath(string attributeName, string columnName = AttributesColumn)
    {
        // Escape single quotes in attribute names
        var escapedAttributeName = attributeName.Replace("'", "''");
        return $"{columnName}->>'{escapedAttributeName}'";
    }

    public static string BuildJsonbAccessor(string attributeName, string columnName = AttributesColumn)
    {
        var escapedAttributeName = attributeName.Replace("'", "''");
        return $"{columnName}->'{escapedAttributeName}'";
    }

    public static string BuildJsonPathParameter(int parameterIndex, string columnName = AttributesColumn)
    {
        return $"{columnName}->> ${parameterIndex}";
    }

    #endregion
}
