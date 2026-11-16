// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Databricks.Features.Infrastructure;

internal static class DatabricksProviderOptions
{
    public const string GeometryType = "geometryType";
}

internal sealed class DatabricksLayerMapping
{
    public required int LayerId { get; init; }

    public required string Table { get; init; }

    public string? Catalog { get; init; }

    public string? Schema { get; init; }

    public required string GeometryColumn { get; init; }

    public required string PrimaryKeyColumn { get; init; }

    public required int Srid { get; init; }

    public required GeometryType GeometryType { get; init; }

    public required IReadOnlyList<string> AttributeColumns { get; init; }

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

        // A shared-table discriminator must constrain every read. This provider does not
        // emit that predicate, so accepting the binding would return other layers' rows.
        if (!string.IsNullOrWhiteSpace(storage.LayerDiscriminatorColumn))
        {
            var column = storage.LayerDiscriminatorColumn.Trim();
            var value = storage.LayerDiscriminatorValue?.ToString(CultureInfo.InvariantCulture) ?? "(missing)";
            throw new ArgumentException(
                $"Databricks layer {layerId} declares storage layer discriminator '{column}' = {value}, "
                + "but this provider cannot enforce a shared-table discriminator. Remove layerDiscriminatorColumn "
                + "from the Metadata v2 storage binding, or bind the layer to a table that contains only this layer's rows.",
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

internal sealed class DatabricksLayerMappingRegistry
{
    private readonly Dictionary<int, DatabricksLayerMapping> _mappings;

    public DatabricksLayerMappingRegistry(IEnumerable<DatabricksLayerMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _mappings = mappings.ToDictionary(static m => m.LayerId);
    }

    public DatabricksLayerMapping Resolve(int layerId)
        => Resolve(layerId, binding: null);

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
