// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.MySql.Features.Infrastructure;

/// <summary>
/// Maps a Honua layer ID to a MySQL/MariaDB table and its column layout.
/// Built from <see cref="MySqlOptions"/> at startup. Unlike PostGIS, the MySQL provider
/// targets user-managed tables — there is no <c>WHERE layer_id = ?</c> discriminator.
/// </summary>
internal sealed class MySqlLayerMapping
{
    /// <summary>Honua layer ID.</summary>
    public required int LayerId { get; init; }

    /// <summary>Physical table or view name (unquoted).</summary>
    public required string TableName { get; init; }

    /// <summary>Optional schema/database qualifier.</summary>
    public string? SchemaName { get; init; }

    /// <summary>Geometry column in the MySQL table.</summary>
    public required string GeometryColumn { get; init; }

    /// <summary>Primary key column name.</summary>
    public required string PrimaryKeyColumn { get; init; }

    /// <summary>SRID declared for the geometry column.</summary>
    public int Srid { get; init; } = SpatialConstants.DefaultSrid;

    /// <summary>Attribute column names (excludes geometry and primary key columns).</summary>
    public required IReadOnlyList<string> AttributeColumns { get; init; }

    /// <summary>Maps attribute column names to their MySQL data types (for catalog field type mapping).</summary>
    public IReadOnlyDictionary<string, string> AttributeColumnTypes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Geometry type (Point, Polygon, ...) declared by configuration.</summary>
    public GeometryType GeometryType { get; init; } = GeometryType.Point;

    /// <summary>Backtick-quoted, schema-qualified table reference for safe SQL interpolation.</summary>
    public string QualifiedTableSql => SchemaName is { Length: > 0 }
        ? $"{MySqlIdentifier.Quote(SchemaName)}.{MySqlIdentifier.Quote(TableName)}"
        : MySqlIdentifier.Quote(TableName);

    /// <summary>Backtick-quoted geometry column reference.</summary>
    public string QuotedGeometryColumn => MySqlIdentifier.Quote(GeometryColumn);

    /// <summary>Backtick-quoted primary key column reference.</summary>
    public string QuotedPrimaryKeyColumn => MySqlIdentifier.Quote(PrimaryKeyColumn);
}
