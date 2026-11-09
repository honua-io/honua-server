// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

public sealed class TableInfo
{
    public required string Schema { get; init; }

    public required string Table { get; init; }

    public string? GeometryColumn { get; init; }

    public string? GeometryType { get; init; }

    public int? Srid { get; init; }

    public long? EstimatedRows { get; init; }

    public List<ColumnInfo> Columns { get; init; } = new();
}

public sealed class ColumnInfo
{
    public required string Name { get; init; }

    public required string DataType { get; init; }

    public bool IsNullable { get; init; }

    public bool IsPrimaryKey { get; init; }

    public int? MaxLength { get; init; }
}

public sealed class TableDiscoveryResponse
{
    public List<TableInfo> Tables { get; init; } = new();
}
