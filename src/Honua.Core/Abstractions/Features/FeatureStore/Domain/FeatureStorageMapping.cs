// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FeatureStore.Domain;

public sealed record FeatureStorageMapping(
    string TableName,
    string? SchemaName = null,
    string? CatalogName = null,
    string? DatabaseName = null,
    string PrimaryKeyColumn = FieldNames.ObjectId,
    string? GeometryColumn = "geometry",
    int? StorageSrid = null,
    string? TemporalColumn = null,
    IReadOnlyDictionary<string, string>? ProviderOptions = null,
    string? AttributesColumn = null,
    string? LayerDiscriminatorColumn = null,
    int? LayerDiscriminatorValue = null)
{
    public const string SourceBackedOption = "sourceBacked";

    public IReadOnlyDictionary<string, string> ProviderOptions { get; init; } =
        ProviderOptions ?? new Dictionary<string, string>();

    public bool IsSourceBacked =>
        ProviderOptions.TryGetValue(SourceBackedOption, out var value)
        && bool.TryParse(value, out var sourceBacked)
        && sourceBacked;

    public const string ManagedFeaturesTableName = "features";

    // A managed write against a layer published over an arbitrary source table is not readable back, so the protocols must refuse it.
    public bool SupportsManagedWrites =>
        !IsSourceBacked
        || (ManagedFeaturesTableName.Equals(TableName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(AttributesColumn, "attributes", StringComparison.OrdinalIgnoreCase)
            && string.Equals(LayerDiscriminatorColumn, "layer_id", StringComparison.OrdinalIgnoreCase)
            && LayerDiscriminatorValue.HasValue);

    public string QualifiedName
    {
        get
        {
            string[] parts =
            [
                DatabaseName ?? string.Empty,
                CatalogName ?? string.Empty,
                SchemaName ?? string.Empty,
                TableName
            ];

            return string.Join(".", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public static FeatureStorageMapping FromMetadata(
        MetadataV2Object resource,
        MetadataV2StorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(storageBinding);

        if (storageBinding.StorageType != MetadataV2StorageType.RelationalTable)
        {
            throw new InvalidOperationException(
                $"Storage binding '{storageBinding.Metadata.Id}' is '{storageBinding.StorageType}', not relational feature storage.");
        }

        var providerOptions = ReadStringOptions(storageBinding.Options);
        var (schemaName, tableName) = storageBinding.ResolveRelationalTable();

        var mapping = new FeatureStorageMapping(
            TableName: tableName ?? string.Empty,
            SchemaName: schemaName,
            CatalogName: storageBinding.ReadStringOption("catalogName")
                ?? storageBinding.ReadStringOption("catalog"),
            DatabaseName: storageBinding.ReadStringOption("databaseName")
                ?? storageBinding.ReadStringOption("database"),
            PrimaryKeyColumn: storageBinding.ReadStringOption("primaryKeyColumn")
                ?? resource.FindPrimaryIdField()?.Name
                ?? FieldNames.ObjectId,
            GeometryColumn: storageBinding.ReadStringOption("geometryColumn")
                ?? resource.FindPrimaryGeometryField()?.Name,
            StorageSrid: storageBinding.ReadIntOption("storageSrid")
                ?? storageBinding.ReadIntOption("srid")
                ?? resource.Spatial?.StorageCrs?.ResolveSrid()
                ?? resource.ReadSrid(),
            TemporalColumn: storageBinding.ReadStringOption("temporalColumn"),
            AttributesColumn: storageBinding.ReadStringOption("attributesColumn"),
            // When the binding advertises a layer-discriminator column (set for layers that
            // share a single physical table, e.g. the shared `features` table keyed by
            // `layer_id`), carry the column name and the bound layer's id so the reader can
            // constrain every query to this layer's rows. The value comes from the binding's
            // StorageLayerId — the same integer the runtime queries by.
            LayerDiscriminatorColumn: storageBinding.ReadStringOption("layerDiscriminatorColumn"),
            LayerDiscriminatorValue: storageBinding.StorageLayerId,
            ProviderOptions: providerOptions);

        var errors = mapping.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Storage binding '{storageBinding.Metadata.Id}' has an invalid feature storage mapping: {string.Join("; ", errors)}");
        }

        return mapping;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(TableName))
        {
            errors.Add("Storage table or view name cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(PrimaryKeyColumn))
        {
            errors.Add("Storage primary key column cannot be empty");
        }

        if (StorageSrid is <= 0)
        {
            errors.Add("Storage SRID must be positive when provided");
        }

        return errors;
    }

    private static Dictionary<string, string> ReadStringOptions(IReadOnlyDictionary<string, JsonElement> options)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            values[option.Key] = option.Value.ValueKind == JsonValueKind.String
                ? option.Value.GetString() ?? string.Empty
                : option.Value.ToString();
        }

        return values;
    }
}
