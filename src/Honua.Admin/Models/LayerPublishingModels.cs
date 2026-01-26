// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Models;

public sealed class TableDiscoveryResponse
{
    public List<TableInfo> Tables { get; init; } = new();
}

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

public sealed class PublishLayerRequest
{
    public required string Schema { get; init; }

    public required string Table { get; init; }

    public required string LayerName { get; init; }

    public string? Description { get; init; }

    public string? GeometryColumn { get; init; }

    public string? GeometryType { get; init; }

    public int? Srid { get; init; }

    public string? PrimaryKey { get; init; }

    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    public string? ServiceName { get; init; }

    public bool Enabled { get; init; } = true;
}

public sealed class LayerEnabledRequest
{
    public bool Enabled { get; init; }
}

public sealed class PublishedLayerSummary
{
    public int LayerId { get; init; }

    public required string LayerName { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public string? Description { get; init; }

    public required string GeometryType { get; init; }

    public int Srid { get; init; }

    public string? PrimaryKey { get; init; }

    public int FieldCount { get; init; }

    public bool Enabled { get; init; }

    public required string ServiceName { get; init; }
}
