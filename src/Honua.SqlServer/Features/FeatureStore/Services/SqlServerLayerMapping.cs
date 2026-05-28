// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Validated SQL Server layer storage binding produced from a <see cref="LayerStorageMapping"/>.
/// All identifiers are pre-validated against an allow-list and bracket-quoted for SQL Server.
/// </summary>
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

    /// <summary>
    /// Bracket-quoted table reference, including catalog/schema qualifiers when configured.
    /// </summary>
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

    /// <summary>
    /// Bracket-quoted primary-key column reference.
    /// </summary>
    public string QuotedPrimaryKeyColumn => SqlServerIdentifier.Quote(PrimaryKeyColumn);

    /// <summary>
    /// Bracket-quoted geometry column reference, or null when the layer has no geometry.
    /// </summary>
    public string? QuotedGeometryColumn =>
        GeometryColumn is null ? null : SqlServerIdentifier.Quote(GeometryColumn);

    /// <summary>
    /// SQL Server type token used for parameters that carry a geometry value.
    /// </summary>
    public string GeometryTypeToken => GeometryColumnType == SqlServerGeometryColumnType.Geography
        ? "geography"
        : "geometry";

    /// <summary>
    /// Builds a <see cref="SqlServerLayerMapping"/> from the provider-neutral storage mapping,
    /// validating identifiers and resolving SQL Server-specific provider options.
    /// </summary>
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

    /// <summary>
    /// Builds a <see cref="SqlServerLayerMapping"/> from a Metadata v2 feature storage mapping,
    /// validating identifiers and resolving SQL Server-specific provider options.
    /// </summary>
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

/// <summary>
/// Native SQL Server spatial column type a layer is stored under.
/// </summary>
internal enum SqlServerGeometryColumnType
{
    /// <summary>
    /// Planar <c>geometry</c> column. Distances and lengths are in CRS units.
    /// </summary>
    Geometry,

    /// <summary>
    /// Geodetic <c>geography</c> column. Distances and lengths are in meters on the spheroid.
    /// </summary>
    Geography
}
