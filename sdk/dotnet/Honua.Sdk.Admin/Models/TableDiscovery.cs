// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Sdk.Admin.Models;

/// <summary>
/// Response from table discovery endpoint.
/// </summary>
public sealed class TableDiscoveryResponse
{
    /// <summary>
    /// List of discovered tables.
    /// </summary>
    [JsonPropertyName("tables")]
    public IReadOnlyList<TableInfo> Tables { get; init; } = [];
}

/// <summary>
/// Information about a discovered table with spatial data.
/// </summary>
public sealed class TableInfo
{
    /// <summary>
    /// Database schema name (e.g., "public").
    /// </summary>
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = string.Empty;

    /// <summary>
    /// Table name.
    /// </summary>
    [JsonPropertyName("table")]
    public string Table { get; init; } = string.Empty;

    /// <summary>
    /// Name of the geometry column.
    /// </summary>
    [JsonPropertyName("geometryColumn")]
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Geometry type (e.g., POINT, POLYGON, MULTIPOLYGON).
    /// </summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial Reference Identifier (SRID).
    /// </summary>
    [JsonPropertyName("srid")]
    public int? Srid { get; init; }

    /// <summary>
    /// Estimated row count.
    /// </summary>
    [JsonPropertyName("estimatedRows")]
    public long? EstimatedRows { get; init; }

    /// <summary>
    /// All columns in the table.
    /// </summary>
    [JsonPropertyName("columns")]
    public IReadOnlyList<ColumnInfo> Columns { get; init; } = [];
}

/// <summary>
/// Information about a table column.
/// </summary>
public sealed class ColumnInfo
{
    /// <summary>
    /// Column name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Column data type.
    /// </summary>
    [JsonPropertyName("dataType")]
    public string DataType { get; init; } = string.Empty;

    /// <summary>
    /// Whether the column allows null values.
    /// </summary>
    [JsonPropertyName("isNullable")]
    public bool IsNullable { get; init; }

    /// <summary>
    /// Whether this is a primary key column.
    /// </summary>
    [JsonPropertyName("isPrimaryKey")]
    public bool IsPrimaryKey { get; init; }

    /// <summary>
    /// Maximum length for character types.
    /// </summary>
    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; init; }
}
