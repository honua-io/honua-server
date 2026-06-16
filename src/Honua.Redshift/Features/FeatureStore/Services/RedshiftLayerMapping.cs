// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Redshift.Features.FeatureStore.Services;

/// <summary>
/// Validated Redshift layer storage binding produced from a <see cref="LayerStorageMapping"/>.
/// All identifiers are pre-validated against an allow-list and double-quoted for Redshift.
/// </summary>
internal sealed record RedshiftLayerMapping
{
    public required int LayerId { get; init; }

    public required string TableName { get; init; }

    public string? SchemaName { get; init; }

    public required string PrimaryKeyColumn { get; init; }

    public string? GeometryColumn { get; init; }

    public int? Srid { get; init; }

    public required RedshiftGeometryColumnType GeometryColumnType { get; init; }

    /// <summary>
    /// Double-quoted table reference, including the schema qualifier when configured.
    /// </summary>
    public string QuotedTableReference
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(SchemaName))
            {
                parts.Add(RedshiftIdentifier.Quote(SchemaName));
            }
            parts.Add(RedshiftIdentifier.Quote(TableName));
            return string.Join('.', parts);
        }
    }

    /// <summary>
    /// Double-quoted primary-key column reference.
    /// </summary>
    public string QuotedPrimaryKeyColumn => RedshiftIdentifier.Quote(PrimaryKeyColumn);

    /// <summary>
    /// Double-quoted geometry column reference, or null when the layer has no geometry.
    /// </summary>
    public string? QuotedGeometryColumn =>
        GeometryColumn is null ? null : RedshiftIdentifier.Quote(GeometryColumn);

    /// <summary>
    /// Builds a <see cref="RedshiftLayerMapping"/> from the provider-neutral storage mapping,
    /// validating identifiers and resolving Redshift-specific provider options.
    /// </summary>
    public static RedshiftLayerMapping FromStorage(int layerId, LayerStorageMapping storage)
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

    /// <summary>
    /// Builds a <see cref="RedshiftLayerMapping"/> from a Metadata v2 feature storage mapping,
    /// validating identifiers and resolving Redshift-specific provider options.
    /// </summary>
    public static RedshiftLayerMapping FromStorage(int layerId, FeatureStorageMapping storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var errors = storage.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid storage mapping for Redshift layer {layerId}: {string.Join("; ", errors)}",
                nameof(storage));
        }

        RedshiftIdentifier.EnsureValid(storage.TableName, "table name");
        if (!string.IsNullOrWhiteSpace(storage.SchemaName))
        {
            RedshiftIdentifier.EnsureValid(storage.SchemaName!, "schema name");
        }
        RedshiftIdentifier.EnsureValid(storage.PrimaryKeyColumn, "primary key column");
        if (!string.IsNullOrWhiteSpace(storage.GeometryColumn))
        {
            RedshiftIdentifier.EnsureValid(storage.GeometryColumn!, "geometry column");
        }

        var columnType = ResolveGeometryColumnType(storage.ProviderOptions);

        return new RedshiftLayerMapping
        {
            LayerId = layerId,
            TableName = storage.TableName,
            SchemaName = storage.SchemaName,
            PrimaryKeyColumn = storage.PrimaryKeyColumn,
            GeometryColumn = storage.GeometryColumn,
            Srid = storage.StorageSrid,
            GeometryColumnType = columnType
        };
    }

    private static RedshiftGeometryColumnType ResolveGeometryColumnType(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue(RedshiftProviderOptions.GeometryType, out var raw))
        {
            return raw.Trim().ToLowerInvariant() switch
            {
                "geometry" => RedshiftGeometryColumnType.Geometry,
                "geography" => RedshiftGeometryColumnType.Geography,
                _ => throw new ArgumentException(
                    $"Unsupported Redshift provider option '{RedshiftProviderOptions.GeometryType}'='{raw}'. Expected 'geometry' or 'geography'.")
            };
        }

        return RedshiftGeometryColumnType.Geometry;
    }
}

/// <summary>
/// Native Redshift spatial column type a layer is stored under.
/// </summary>
internal enum RedshiftGeometryColumnType
{
    /// <summary>
    /// Planar <c>GEOMETRY</c> column. Distances and lengths are in CRS units.
    /// </summary>
    Geometry,

    /// <summary>
    /// Geodetic <c>GEOGRAPHY</c> column. Distances and lengths are in meters on the spheroid.
    /// </summary>
    Geography
}
