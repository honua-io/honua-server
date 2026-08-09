// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Validated Oracle layer storage binding produced from a <see cref="FeatureStorageMapping"/>.
/// All identifiers are pre-validated against an allow-list and double-quoted for Oracle.
/// </summary>
internal sealed record OracleLayerMapping
{
    /// <summary>Storage layer identifier this mapping serves.</summary>
    public required int LayerId { get; init; }

    /// <summary>Physical table name (validated, unquoted).</summary>
    public required string TableName { get; init; }

    /// <summary>Optional schema/owner qualifier.</summary>
    public string? SchemaName { get; init; }

    /// <summary>Primary-key/object-identifier column name (validated, unquoted).</summary>
    public required string PrimaryKeyColumn { get; init; }

    /// <summary>Geometry column name, or null when the layer has no geometry.</summary>
    public string? GeometryColumn { get; init; }

    /// <summary>SRID of the stored geometry, when configured.</summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Double-quoted table reference, including the schema/owner qualifier when configured.
    /// </summary>
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

    /// <summary>
    /// Double-quoted primary-key column reference.
    /// </summary>
    public string QuotedPrimaryKeyColumn => OracleIdentifier.Quote(PrimaryKeyColumn);

    /// <summary>
    /// Double-quoted geometry column reference, or null when the layer has no geometry.
    /// </summary>
    public string? QuotedGeometryColumn =>
        GeometryColumn is null ? null : OracleIdentifier.Quote(GeometryColumn);

    /// <summary>
    /// Builds an <see cref="OracleLayerMapping"/> from the provider-neutral storage mapping,
    /// validating identifiers.
    /// </summary>
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

    /// <summary>
    /// Builds an <see cref="OracleLayerMapping"/> from a Metadata v2 feature storage mapping,
    /// validating identifiers.
    /// </summary>
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
