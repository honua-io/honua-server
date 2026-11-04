// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.MySql.Features.Infrastructure;

internal sealed class MySqlLayerMapping
{
    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public string? SchemaName { get; init; }

    public required string GeometryColumn { get; init; }

    public required string PrimaryKeyColumn { get; init; }

    public int Srid { get; init; } = SpatialConstants.DefaultSrid;

    public required IReadOnlyList<string> AttributeColumns { get; init; }

    public IReadOnlyDictionary<string, string> AttributeColumnTypes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public GeometryType GeometryType { get; init; } = GeometryType.Point;

    public string QualifiedTableSql => SchemaName is { Length: > 0 }
        ? $"{MySqlIdentifier.Quote(SchemaName)}.{MySqlIdentifier.Quote(TableName)}"
        : MySqlIdentifier.Quote(TableName);

    public string QuotedGeometryColumn => MySqlIdentifier.Quote(GeometryColumn);

    public string QuotedPrimaryKeyColumn => MySqlIdentifier.Quote(PrimaryKeyColumn);
}
