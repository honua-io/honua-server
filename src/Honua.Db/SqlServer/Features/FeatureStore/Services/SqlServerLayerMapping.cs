// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.SqlServer.Features.FeatureStore.Services;

internal sealed record SqlServerLayerMapping
{
    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public string? SchemaName { get; init; }

    public string? CatalogName { get; init; }

    public required string PrimaryKeyColumn { get; init; }

    public string? GeometryColumn { get; init; }

    public int? Srid { get; init; }

    public required SqlServerGeometryColumnType GeometryColumnType { get; init; }

    public string QuotedTableReference
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(CatalogName))
            {
                parts.Add(SqlServerIdentifier.Quote(CatalogName));
            }
            parts.Add(SqlServerIdentifier.Quote(string.IsNullOrWhiteSpace(SchemaName) ? "dbo" : SchemaName));
            parts.Add(SqlServerIdentifier.Quote(TableName));
            return string.Join('.', parts);
        }
    }

    public string QuotedPrimaryKeyColumn => SqlServerIdentifier.Quote(PrimaryKeyColumn);

    public string? QuotedGeometryColumn =>
        GeometryColumn is null ? null : SqlServerIdentifier.Quote(GeometryColumn);

    public string GeometryTypeToken => GeometryColumnType == SqlServerGeometryColumnType.Geography
        ? "geography"
        : "geometry";

    public static SqlServerLayerMapping FromStorage(int layerId, LayerStorageMapping storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return FromStorage(
            layerId,
            new FeatureStorageMapping(
                TableName: storage.TableName,
                SchemaName: storage.SchemaName,
                CatalogName: storage.CatalogName,
                DatabaseName: storage.DatabaseName,
                PrimaryKeyColumn: storage.PrimaryKeyColumn,
                GeometryColumn: storage.GeometryColumn,
                StorageSrid: storage.StorageSrid,
                TemporalColumn: storage.TemporalColumn,
                ProviderOptions: storage.ProviderOptions));
    }

    public static SqlServerLayerMapping FromStorage(int layerId, FeatureStorageMapping storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var errors = storage.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid storage mapping for SQL Server layer {layerId}: {string.Join("; ", errors)}",
                nameof(storage));
        }

        SqlServerIdentifier.EnsureValid(storage.TableName, "table name");
        if (!string.IsNullOrWhiteSpace(storage.SchemaName))
        {
            SqlServerIdentifier.EnsureValid(storage.SchemaName!, "schema name");
        }
        if (!string.IsNullOrWhiteSpace(storage.CatalogName))
        {
            SqlServerIdentifier.EnsureValid(storage.CatalogName!, "catalog name");
        }
        SqlServerIdentifier.EnsureValid(storage.PrimaryKeyColumn, "primary key column");
        if (!string.IsNullOrWhiteSpace(storage.GeometryColumn))
        {
            SqlServerIdentifier.EnsureValid(storage.GeometryColumn!, "geometry column");
        }

        var columnType = ResolveGeometryColumnType(storage.ProviderOptions);

        return new SqlServerLayerMapping
        {
            LayerId = layerId,
            TableName = storage.TableName,
            SchemaName = storage.SchemaName,
            CatalogName = storage.CatalogName,
            PrimaryKeyColumn = storage.PrimaryKeyColumn,
            GeometryColumn = storage.GeometryColumn,
            Srid = storage.StorageSrid,
            GeometryColumnType = columnType
        };
    }

    private static SqlServerGeometryColumnType ResolveGeometryColumnType(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue(SqlServerProviderOptions.GeometryType, out var raw))
        {
            return raw.Trim().ToLowerInvariant() switch
            {
                "geometry" => SqlServerGeometryColumnType.Geometry,
                "geography" => SqlServerGeometryColumnType.Geography,
                _ => throw new ArgumentException(
                    $"Unsupported SQL Server provider option '{SqlServerProviderOptions.GeometryType}'='{raw}'. Expected 'geometry' or 'geography'.")
            };
        }

        return SqlServerGeometryColumnType.Geometry;
    }
}

internal enum SqlServerGeometryColumnType
{
    Geometry,

    Geography
}
