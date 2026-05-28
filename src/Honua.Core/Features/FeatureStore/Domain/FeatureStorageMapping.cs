// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Provider-neutral runtime binding from a Metadata v2 resource to physical feature storage.
/// </summary>
/// <param name="TableName">Physical table or view name.</param>
/// <param name="SchemaName">Optional schema qualifier.</param>
/// <param name="CatalogName">Optional catalog qualifier for engines that expose catalogs.</param>
/// <param name="DatabaseName">Optional database qualifier for engines that route across databases.</param>
/// <param name="PrimaryKeyColumn">Primary key or stable object identifier column.</param>
/// <param name="GeometryColumn">Geometry column, or null for non-spatial tables.</param>
/// <param name="StorageSrid">SRID/CRS used by the stored geometry.</param>
/// <param name="TemporalColumn">Optional temporal column used for time-aware resources.</param>
/// <param name="AttributesColumn">Optional JSONB column that stores schema fields as a single
/// document (e.g. the seed's shared <c>features</c> table). When set, the Postgres feature
/// reader projects schema fields via <c>{AttributesColumn}-&gt;&gt;'fieldname'</c> instead of
/// expecting a column per field.</param>
/// <param name="ProviderOptions">Provider-specific extension values when a neutral field is not enough.</param>
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
    string? AttributesColumn = null)
{
    /// <summary>
    /// Provider option key indicating that reads should be routed back to the source table.
    /// </summary>
    public const string SourceBackedOption = "sourceBacked";

    /// <summary>
    /// Provider-specific extension values, normalized to an empty dictionary when unset.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderOptions { get; init; } =
        ProviderOptions ?? new Dictionary<string, string>();

    /// <summary>
    /// Gets a value indicating whether the mapping should be read from the source provider.
    /// </summary>
    public bool IsSourceBacked =>
        ProviderOptions.TryGetValue(SourceBackedOption, out var value)
        && bool.TryParse(value, out var sourceBacked)
        && sourceBacked;

    /// <summary>
    /// Gets the best available fully qualified storage name for diagnostics and capability reporting.
    /// </summary>
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

    /// <summary>
    /// Builds a feature storage mapping from a Metadata v2 resource and storage binding.
    /// </summary>
    /// <param name="resource">Canonical Metadata v2 resource.</param>
    /// <param name="storageBinding">Storage binding that materializes the resource.</param>
    /// <returns>Resolved feature storage mapping.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the binding cannot be used as relational feature storage.</exception>
    public static FeatureStorageMapping FromMetadata(
        MetadataV2Resource resource,
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
        var schemaName = ReadStringOption(storageBinding.Options, "schemaName")
            ?? ReadStringOption(storageBinding.Options, "schema");
        var tableName = ReadStringOption(storageBinding.Options, "tableName")
            ?? ReadStringOption(storageBinding.Options, "table");

        if (string.IsNullOrWhiteSpace(tableName))
        {
            var parsed = ParseRelationalLocator(storageBinding.Locator);
            schemaName ??= parsed.SchemaName;
            tableName = parsed.TableName;
        }

        var mapping = new FeatureStorageMapping(
            TableName: tableName ?? string.Empty,
            SchemaName: schemaName,
            CatalogName: ReadStringOption(storageBinding.Options, "catalogName")
                ?? ReadStringOption(storageBinding.Options, "catalog"),
            DatabaseName: ReadStringOption(storageBinding.Options, "databaseName")
                ?? ReadStringOption(storageBinding.Options, "database"),
            PrimaryKeyColumn: ReadStringOption(storageBinding.Options, "primaryKeyColumn")
                ?? resource.FindPrimaryIdField()?.Name
                ?? FieldNames.ObjectId,
            GeometryColumn: ReadStringOption(storageBinding.Options, "geometryColumn")
                ?? resource.FindPrimaryGeometryField()?.Name,
            StorageSrid: ReadIntOption(storageBinding.Options, "storageSrid")
                ?? ReadIntOption(storageBinding.Options, "srid")
                ?? resource.Spatial?.StorageCrs?.ResolveSrid()
                ?? resource.ReadSrid(),
            TemporalColumn: ReadStringOption(storageBinding.Options, "temporalColumn"),
            AttributesColumn: ReadStringOption(storageBinding.Options, "attributesColumn"),
            ProviderOptions: providerOptions);

        var errors = mapping.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Storage binding '{storageBinding.Metadata.Id}' has an invalid feature storage mapping: {string.Join("; ", errors)}");
        }

        return mapping;
    }

    /// <summary>
    /// Validates the storage mapping.
    /// </summary>
    /// <returns>Validation error messages, empty when valid.</returns>
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

    private static string? ReadStringOption(IReadOnlyDictionary<string, JsonElement> options, string key)
    {
        if (!options.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static int? ReadIntOption(IReadOnlyDictionary<string, JsonElement> options, string key)
    {
        if (!options.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static (string? SchemaName, string? TableName) ParseRelationalLocator(string? locator)
    {
        var trimmed = locator?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains(':', StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal))
        {
            return (null, null);
        }

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (null, null),
            1 => (null, UnquoteIdentifier(parts[0])),
            _ => (UnquoteIdentifier(parts[^2]), UnquoteIdentifier(parts[^1]))
        };
    }

    private static string UnquoteIdentifier(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : trimmed;
    }
}
