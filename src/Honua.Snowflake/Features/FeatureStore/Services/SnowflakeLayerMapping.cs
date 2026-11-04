// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Snowflake.Features.FeatureStore.Services;

internal sealed record SnowflakeLayerMapping
{
    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public string? SchemaName { get; init; }

    public string? DatabaseName { get; init; }

    public required string PrimaryKeyColumn { get; init; }

    public string? GeometryColumn { get; init; }

    public int? Srid { get; init; }

    public required SnowflakeGeometryColumnType GeometryColumnType { get; init; }

    public string QuotedTableReference
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(DatabaseName))
            {
                parts.Add(SnowflakeIdentifier.Quote(DatabaseName));
            }
            if (!string.IsNullOrWhiteSpace(SchemaName))
            {
                parts.Add(SnowflakeIdentifier.Quote(SchemaName));
            }
            parts.Add(SnowflakeIdentifier.Quote(TableName));
            return string.Join('.', parts);
        }
    }

    public string QuotedPrimaryKeyColumn => SnowflakeIdentifier.Quote(PrimaryKeyColumn);

    public string? QuotedGeometryColumn =>
        GeometryColumn is null ? null : SnowflakeIdentifier.Quote(GeometryColumn);

    public string SpatialConstructor => GeometryColumnType == SnowflakeGeometryColumnType.Geometry
        ? "TO_GEOMETRY"
        : "TO_GEOGRAPHY";

    public static SnowflakeLayerMapping FromStorage(int layerId, LayerStorageMapping storage)
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

    public static SnowflakeLayerMapping FromStorage(int layerId, FeatureStorageMapping storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var errors = storage.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid storage mapping for Snowflake layer {layerId}: {string.Join("; ", errors)}",
                nameof(storage));
        }

        SnowflakeIdentifier.EnsureValid(storage.TableName, "table name");
        if (!string.IsNullOrWhiteSpace(storage.SchemaName))
        {
            SnowflakeIdentifier.EnsureValid(storage.SchemaName!, "schema name");
        }
        if (!string.IsNullOrWhiteSpace(storage.DatabaseName))
        {
            SnowflakeIdentifier.EnsureValid(storage.DatabaseName!, "database name");
        }
        SnowflakeIdentifier.EnsureValid(storage.PrimaryKeyColumn, "primary key column");
        if (!string.IsNullOrWhiteSpace(storage.GeometryColumn))
        {
            SnowflakeIdentifier.EnsureValid(storage.GeometryColumn!, "geometry column");
        }

        var columnType = ResolveGeometryColumnType(storage.ProviderOptions);

        return new SnowflakeLayerMapping
        {
            LayerId = layerId,
            TableName = storage.TableName,
            SchemaName = storage.SchemaName,
            // Snowflake routes by database.schema.table; the provider-neutral mapping carries
            // the database directly and the (Snowflake-irrelevant) catalog separately. Prefer
            // DatabaseName, falling back to CatalogName for callers that populated only the
            // generic catalog qualifier.
            DatabaseName = storage.DatabaseName ?? storage.CatalogName,
            PrimaryKeyColumn = storage.PrimaryKeyColumn,
            GeometryColumn = storage.GeometryColumn,
            Srid = storage.StorageSrid,
            GeometryColumnType = columnType
        };
    }

    private static SnowflakeGeometryColumnType ResolveGeometryColumnType(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue(SnowflakeProviderOptions.GeometryType, out var raw))
        {
            return raw.Trim().ToLowerInvariant() switch
            {
                "geometry" => SnowflakeGeometryColumnType.Geometry,
                "geography" => SnowflakeGeometryColumnType.Geography,
                _ => throw new ArgumentException(
                    $"Unsupported Snowflake provider option '{SnowflakeProviderOptions.GeometryType}'='{raw}'. Expected 'geometry' or 'geography'.")
            };
        }

        // Snowflake's flagship spatial type is GEOGRAPHY (WGS84); default to it.
        return SnowflakeGeometryColumnType.Geography;
    }
}

internal enum SnowflakeGeometryColumnType
{
    Geography,

    Geometry
}
