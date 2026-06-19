// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Snowflake.Features.FeatureStore.Services;

/// <summary>
/// Validated Snowflake layer storage binding produced from a <see cref="LayerStorageMapping"/> or
/// a Metadata v2 <see cref="FeatureStorageMapping"/>. All identifiers are pre-validated against an
/// allow-list and double-quoted for Snowflake.
/// </summary>
internal sealed record SnowflakeLayerMapping
{
    /// <summary>Integer storage-layer handle used by the feature-reader APIs.</summary>
    public required int LayerId { get; init; }

    /// <summary>Physical table or view name.</summary>
    public required string TableName { get; init; }

    /// <summary>Optional schema qualifier.</summary>
    public string? SchemaName { get; init; }

    /// <summary>Optional database qualifier.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>Primary key or stable object identifier column.</summary>
    public required string PrimaryKeyColumn { get; init; }

    /// <summary>Geometry column, or null for non-spatial tables.</summary>
    public string? GeometryColumn { get; init; }

    /// <summary>SRID/CRS used by the stored geometry, when known.</summary>
    public int? Srid { get; init; }

    /// <summary>Native Snowflake spatial column type the layer is stored under.</summary>
    public required SnowflakeGeometryColumnType GeometryColumnType { get; init; }

    /// <summary>
    /// Double-quoted table reference, including database/schema qualifiers when configured.
    /// </summary>
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

    /// <summary>Double-quoted primary-key column reference.</summary>
    public string QuotedPrimaryKeyColumn => SnowflakeIdentifier.Quote(PrimaryKeyColumn);

    /// <summary>Double-quoted geometry column reference, or null when the layer has no geometry.</summary>
    public string? QuotedGeometryColumn =>
        GeometryColumn is null ? null : SnowflakeIdentifier.Quote(GeometryColumn);

    /// <summary>
    /// Snowflake constructor function used to build a spatial value from WKB:
    /// <c>TO_GEOGRAPHY</c> for geodetic columns, <c>TO_GEOMETRY</c> for planar columns.
    /// </summary>
    public string SpatialConstructor => GeometryColumnType == SnowflakeGeometryColumnType.Geometry
        ? "TO_GEOMETRY"
        : "TO_GEOGRAPHY";

    /// <summary>
    /// Builds a <see cref="SnowflakeLayerMapping"/> from the provider-neutral storage mapping,
    /// validating identifiers and resolving Snowflake-specific provider options.
    /// </summary>
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

    /// <summary>
    /// Builds a <see cref="SnowflakeLayerMapping"/> from a Metadata v2 feature storage mapping,
    /// validating identifiers and resolving Snowflake-specific provider options.
    /// </summary>
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

/// <summary>
/// Native Snowflake spatial column type a layer is stored under.
/// </summary>
internal enum SnowflakeGeometryColumnType
{
    /// <summary>
    /// Geodetic <c>GEOGRAPHY</c> column. Coordinates are WGS84 longitude/latitude; distances and
    /// lengths are computed on the sphere in meters.
    /// </summary>
    Geography,

    /// <summary>
    /// Planar <c>GEOMETRY</c> column. Distances and lengths are in CRS units.
    /// </summary>
    Geometry
}
