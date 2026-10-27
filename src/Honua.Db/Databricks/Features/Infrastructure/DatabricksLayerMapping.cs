// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Databricks.Features.Infrastructure;

/// <summary>
/// Provider-option keys recognized on a Metadata v2 <see cref="FeatureStorageMapping"/>.
/// </summary>
internal static class DatabricksProviderOptions
{
    /// <summary>
    /// Declared OGC geometry type (<c>Point</c>, <c>Polygon</c>, and the other
    /// <see cref="GeometryType"/> names). Defaults to <see cref="GeometryType.Point"/>.
    /// </summary>
    public const string GeometryType = "geometryType";
}

/// <summary>
/// Resolved physical mapping for a single Databricks-backed layer. Built from
/// <c>Databricks:Layers</c> or from a Metadata v2 <see cref="FeatureStorageMapping"/>
/// when the layer's connection provider is Databricks. Carries the fully qualified
/// table reference and the column names used to emit SQL against the SQL Warehouse.
/// </summary>
internal sealed class DatabricksLayerMapping
{
    /// <summary>Honua layer ID.</summary>
    public required int LayerId { get; init; }

    /// <summary>Table or view name.</summary>
    public required string Table { get; init; }

    /// <summary>Optional catalog qualifier (resolved layer-over-global).</summary>
    public string? Catalog { get; init; }

    /// <summary>Optional schema qualifier (resolved layer-over-global).</summary>
    public string? Schema { get; init; }

    /// <summary>Geometry column name.</summary>
    public required string GeometryColumn { get; init; }

    /// <summary>Primary-key column name.</summary>
    public required string PrimaryKeyColumn { get; init; }

    /// <summary>Stored geometry SRID.</summary>
    public required int Srid { get; init; }

    /// <summary>Declared geometry type.</summary>
    public required GeometryType GeometryType { get; init; }

    /// <summary>Attribute column names exposed by the layer.</summary>
    public required IReadOnlyList<string> AttributeColumns { get; init; }

    /// <summary>
    /// Builds a <see cref="DatabricksLayerMapping"/> from a Metadata v2 feature storage
    /// mapping, validating catalog, schema, table, geometry, and object-id identifiers.
    /// </summary>
    /// <param name="layerId">Honua layer id.</param>
    /// <param name="storage">Provider-neutral storage mapping.</param>
    /// <returns>Validated layer mapping. Attribute columns are empty until the resource schema is applied.</returns>
    public static DatabricksLayerMapping FromStorage(int layerId, FeatureStorageMapping storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var errors = storage.Validate().ToList();
        if (string.IsNullOrWhiteSpace(storage.GeometryColumn))
        {
            errors.Add("Storage geometry column cannot be empty");
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid storage mapping for Databricks layer {layerId}: {string.Join("; ", errors)}",
                nameof(storage));
        }

        DatabricksIdentifier.ValidateIdentifier(storage.TableName);
        if (!string.IsNullOrWhiteSpace(storage.SchemaName))
        {
            DatabricksIdentifier.ValidateIdentifier(storage.SchemaName);
        }

        if (!string.IsNullOrWhiteSpace(storage.CatalogName))
        {
            DatabricksIdentifier.ValidateIdentifier(storage.CatalogName);
        }

        DatabricksIdentifier.ValidateIdentifier(storage.PrimaryKeyColumn);
        DatabricksIdentifier.ValidateIdentifier(storage.GeometryColumn!);

        return new DatabricksLayerMapping
        {
            LayerId = layerId,
            Table = storage.TableName,
            Catalog = NullIfBlank(storage.CatalogName),
            Schema = NullIfBlank(storage.SchemaName),
            GeometryColumn = storage.GeometryColumn!,
            PrimaryKeyColumn = storage.PrimaryKeyColumn,
            Srid = storage.StorageSrid ?? SpatialConstants.DefaultSrid,
            GeometryType = ResolveGeometryType(storage.ProviderOptions),
            AttributeColumns = []
        };
    }

    /// <summary>
    /// Returns a copy whose attribute columns and, when <paramref name="geometryType"/> is set,
    /// geometry type come from the Metadata v2 resource schema.
    /// </summary>
    /// <param name="attributeColumns">Attribute columns declared by the resource.</param>
    /// <param name="geometryType">Resource geometry type, or null to keep the storage mapping's type.</param>
    /// <returns>Copy with the schema applied.</returns>
    public DatabricksLayerMapping WithSchema(IReadOnlyList<string> attributeColumns, GeometryType? geometryType)
    {
        ArgumentNullException.ThrowIfNull(attributeColumns);
        return new DatabricksLayerMapping
        {
            LayerId = LayerId,
            Table = Table,
            Catalog = Catalog,
            Schema = Schema,
            GeometryColumn = GeometryColumn,
            PrimaryKeyColumn = PrimaryKeyColumn,
            Srid = Srid,
            GeometryType = geometryType ?? GeometryType,
            AttributeColumns = attributeColumns
        };
    }

    /// <summary>
    /// Builds the backtick-quoted, fully qualified table reference
    /// (<c>`catalog`.`schema`.`table`</c>) honoring any configured catalog/schema.
    /// </summary>
    public string QualifiedTable()
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(Catalog))
        {
            parts.Add(DatabricksIdentifier.Quote(Catalog));
        }

        if (!string.IsNullOrWhiteSpace(Schema))
        {
            parts.Add(DatabricksIdentifier.Quote(Schema));
        }

        parts.Add(DatabricksIdentifier.Quote(Table));
        return string.Join('.', parts);
    }

    private static GeometryType ResolveGeometryType(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue(DatabricksProviderOptions.GeometryType, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return GeometryType.Point;
        }

        if (!Enum.TryParse<GeometryType>(raw.Trim(), ignoreCase: true, out var parsed)
            || parsed == GeometryType.None)
        {
            throw new ArgumentException(
                $"Unsupported Databricks provider option '{DatabricksProviderOptions.GeometryType}'='{raw}'. Expected an OGC geometry type name.");
        }

        return parsed;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Registry of Databricks layer mappings keyed by Honua layer id. A Metadata v2 binding
/// whose connection provider is <c>databricks</c> (aliases <c>databrickssql</c>, <c>dbsql</c>)
/// wins over <c>Databricks:Layers</c>.
/// </summary>
internal sealed class DatabricksLayerMappingRegistry
{
    private readonly Dictionary<int, DatabricksLayerMapping> _mappings;

    /// <summary>
    /// Initializes the registry from the resolved layer mappings.
    /// </summary>
    /// <param name="mappings">Resolved layer mappings.</param>
    public DatabricksLayerMappingRegistry(IEnumerable<DatabricksLayerMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _mappings = mappings.ToDictionary(static m => m.LayerId);
    }

    /// <summary>
    /// Resolves the mapping for <paramref name="layerId"/> from static configuration.
    /// </summary>
    /// <param name="layerId">Honua layer id.</param>
    /// <returns>Resolved layer mapping.</returns>
    public DatabricksLayerMapping Resolve(int layerId)
        => Resolve(layerId, binding: null);

    /// <summary>
    /// Resolves <paramref name="layerId"/> from <paramref name="binding"/> when that binding's
    /// connection provider is Databricks; otherwise falls back to <c>Databricks:Layers</c>.
    /// </summary>
    /// <param name="layerId">Honua layer id.</param>
    /// <param name="binding">Metadata v2 provider binding, or null when the read is not routed.</param>
    /// <returns>Resolved layer mapping.</returns>
    public DatabricksLayerMapping Resolve(int layerId, FeatureProviderBinding? binding)
    {
        if (binding is not null && UsesMetadataBinding(binding))
        {
            if (binding.StorageLayerId != layerId)
            {
                throw new InvalidOperationException(
                    $"Databricks provider binding targets storage layer {binding.StorageLayerId}, not requested layer {layerId}.");
            }

            var mapping = DatabricksLayerMapping.FromStorage(layerId, binding.StorageMapping);
            return mapping.WithSchema(AttributeColumnsFrom(binding, mapping), GeometryTypeFromResource(binding));
        }

        return _mappings.TryGetValue(layerId, out var configured)
            ? configured
            : throw new InvalidOperationException($"No Databricks layer mapping configured for layer {layerId}.");
    }

    private static bool UsesMetadataBinding(FeatureProviderBinding binding)
    {
        var provider = binding.Connection is null
            ? binding.Provider.ProviderName
            : binding.Connection.NormalizedProvider;
        return string.Equals(
            DataProviderNames.Normalize(provider),
            DataProviderNames.Databricks,
            StringComparison.Ordinal);
    }

    private static List<string> AttributeColumnsFrom(FeatureProviderBinding binding, DatabricksLayerMapping mapping)
    {
        var columns = new List<string>();
        foreach (var field in binding.Resource.SchemaFields)
        {
            if (field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
            {
                continue;
            }

            if (field.Name.Equals(mapping.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals(mapping.GeometryColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DatabricksIdentifier.ValidateAttributeName(field.Name);
            columns.Add(field.Name);
        }

        return columns;
    }

    private static GeometryType? GeometryTypeFromResource(FeatureProviderBinding binding)
    {
        if (binding.StorageMapping.ProviderOptions.ContainsKey(DatabricksProviderOptions.GeometryType))
        {
            return null;
        }

        return binding.Resource.Spatial?.GeometryType switch
        {
            MetadataV2GeometryType.Point => GeometryType.Point,
            MetadataV2GeometryType.MultiPoint => GeometryType.MultiPoint,
            MetadataV2GeometryType.LineString => GeometryType.LineString,
            MetadataV2GeometryType.MultiLineString => GeometryType.MultiLineString,
            MetadataV2GeometryType.Polygon => GeometryType.Polygon,
            MetadataV2GeometryType.MultiPolygon => GeometryType.MultiPolygon,
            MetadataV2GeometryType.GeometryCollection or MetadataV2GeometryType.Mixed => GeometryType.GeometryCollection,
            _ => null
        };
    }
}
