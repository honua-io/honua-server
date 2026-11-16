// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Oracle.Features.FeatureStore.Services;

internal sealed record OracleLayerMapping
{
    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public string? SchemaName { get; init; }

    public required string PrimaryKeyColumn { get; init; }

    public string? GeometryColumn { get; init; }

    public int? Srid { get; init; }

    public string QuotedTableReference
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SchemaName))
            {
                return OracleIdentifier.Quote(TableName);
            }
            return OracleIdentifier.Quote(SchemaName) + "." + OracleIdentifier.Quote(TableName);
        }
    }

    public string QuotedPrimaryKeyColumn => OracleIdentifier.Quote(PrimaryKeyColumn);

    public string? QuotedGeometryColumn =>
        GeometryColumn is null ? null : OracleIdentifier.Quote(GeometryColumn);

    public static OracleLayerMapping FromStorage(int layerId, LayerStorageMapping storage)
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

    public static OracleLayerMapping FromStorage(int layerId, FeatureStorageMapping storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var errors = storage.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid storage mapping for Oracle layer {layerId}: {string.Join("; ", errors)}",
                nameof(storage));
        }

        OracleIdentifier.EnsureValid(storage.TableName, "table name");
        if (!string.IsNullOrWhiteSpace(storage.SchemaName))
        {
            OracleIdentifier.EnsureValid(storage.SchemaName!, "schema name");
        }
        OracleIdentifier.EnsureValid(storage.PrimaryKeyColumn, "primary key column");
        if (!string.IsNullOrWhiteSpace(storage.GeometryColumn))
        {
            OracleIdentifier.EnsureValid(storage.GeometryColumn!, "geometry column");
        }

        return new OracleLayerMapping
        {
            LayerId = layerId,
            TableName = storage.TableName,
            SchemaName = storage.SchemaName,
            PrimaryKeyColumn = storage.PrimaryKeyColumn,
            GeometryColumn = storage.GeometryColumn,
            Srid = storage.StorageSrid
        };
    }
}
