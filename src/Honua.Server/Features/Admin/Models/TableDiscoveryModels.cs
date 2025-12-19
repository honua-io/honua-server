// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Information about a discovered table with spatial data
/// </summary>
public sealed class TableInfo
{
    /// <summary>
    /// Database schema name (e.g., "public")
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Table name
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Name of the geometry column
    /// </summary>
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Geometry type (e.g., POINT, POLYGON, MULTIPOLYGON)
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial Reference Identifier (SRID)
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Estimated row count
    /// </summary>
    public long? EstimatedRows { get; init; }

    /// <summary>
    /// All columns in the table
    /// </summary>
    public List<ColumnInfo> Columns { get; init; } = new();
}

/// <summary>
/// Information about a table column
/// </summary>
public sealed class ColumnInfo
{
    /// <summary>
    /// Column name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Column data type
    /// </summary>
    public required string DataType { get; init; }

    /// <summary>
    /// Whether the column allows null values
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Whether this is a primary key column
    /// </summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>
    /// Maximum length for character types
    /// </summary>
    public int? MaxLength { get; init; }
}

/// <summary>
/// Response from table discovery endpoint
/// </summary>
public sealed class TableDiscoveryResponse
{
    /// <summary>
    /// List of discovered tables
    /// </summary>
    public List<TableInfo> Tables { get; init; } = new();
}

/// <summary>
/// Response model with JSON source generation for AOT compatibility
/// </summary>
[JsonSerializable(typeof(TableDiscoveryResponse))]
[JsonSerializable(typeof(TableInfo))]
[JsonSerializable(typeof(ColumnInfo))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class TableDiscoveryJsonContext : JsonSerializerContext
{
}
