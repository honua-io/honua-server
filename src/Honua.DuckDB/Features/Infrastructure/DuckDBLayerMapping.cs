// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// Maps a Honua layer ID to a DuckDB table and its column layout.
/// Populated from configuration and validated at startup via schema introspection.
/// </summary>
internal sealed class DuckDBLayerMapping
{
    /// <summary>Honua layer ID.</summary>
    public required int LayerId { get; init; }

    /// <summary>DuckDB table name (unquoted).</summary>
    public required string TableName { get; init; }

    /// <summary>Geometry column in the DuckDB table.</summary>
    public required string GeometryColumn { get; init; }

    /// <summary>Object ID / primary key column.</summary>
    public required string ObjectIdColumn { get; init; }

    /// <summary>SRID declared for the geometry column.</summary>
    public int Srid { get; init; } = 4326;

    /// <summary>Attribute column names (excludes geometry and object ID columns).</summary>
    public required IReadOnlyList<string> AttributeColumns { get; init; }

    /// <summary>Maps attribute column names to their DuckDB data types (for catalog field type mapping).</summary>
    public IReadOnlyDictionary<string, string> AttributeColumnTypes { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Gets the double-quoted table name for safe SQL interpolation.</summary>
    public string QuotedTableName => $"\"{TableName}\"";

    /// <summary>Gets the double-quoted geometry column for safe SQL interpolation.</summary>
    public string QuotedGeometryColumn => $"\"{GeometryColumn}\"";

    /// <summary>Gets the double-quoted object ID column for safe SQL interpolation.</summary>
    public string QuotedObjectIdColumn => $"\"{ObjectIdColumn}\"";
}
