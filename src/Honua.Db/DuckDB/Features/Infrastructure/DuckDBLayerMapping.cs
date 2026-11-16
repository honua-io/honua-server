// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Db.DuckDB.Features.Infrastructure;

internal sealed class DuckDBLayerMapping
{
    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public required string GeometryColumn { get; init; }

    public required string ObjectIdColumn { get; init; }

    public int Srid { get; init; } = SpatialConstants.DefaultSrid;

    public required IReadOnlyList<string> AttributeColumns { get; init; }

    public IReadOnlyDictionary<string, string> AttributeColumnTypes { get; init; } =
        new Dictionary<string, string>();

    public string QuotedTableName => DuckDBExternalSourceSql.QuoteIdentifier(TableName);

    public string QuotedGeometryColumn => DuckDBExternalSourceSql.QuoteIdentifier(GeometryColumn);

    public string QuotedObjectIdColumn => DuckDBExternalSourceSql.QuoteIdentifier(ObjectIdColumn);
}
