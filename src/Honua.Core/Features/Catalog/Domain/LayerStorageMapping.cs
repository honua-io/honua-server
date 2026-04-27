// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Provider-neutral runtime binding from a Honua layer to physical feature storage.
/// </summary>
/// <param name="TableName">Physical table or view name.</param>
/// <param name="SchemaName">Optional schema qualifier.</param>
/// <param name="CatalogName">Optional catalog qualifier for engines that expose catalogs.</param>
/// <param name="DatabaseName">Optional database qualifier for engines that route across databases.</param>
/// <param name="PrimaryKeyColumn">Primary key or stable object identifier column.</param>
/// <param name="GeometryColumn">Geometry column, or null for non-spatial tables.</param>
/// <param name="StorageSrid">SRID/CRS used by the stored geometry.</param>
/// <param name="TemporalColumn">Optional temporal column used for time-aware layers.</param>
/// <param name="ProviderOptions">Provider-specific extension values when a neutral field is not enough.</param>
public sealed record LayerStorageMapping(
    string TableName,
    string? SchemaName = null,
    string? CatalogName = null,
    string? DatabaseName = null,
    string PrimaryKeyColumn = FieldNames.ObjectId,
    string? GeometryColumn = "geometry",
    int? StorageSrid = null,
    string? TemporalColumn = null,
    IReadOnlyDictionary<string, string>? ProviderOptions = null)
{
    /// <summary>
    /// Provider-specific extension values, normalized to an empty dictionary when unset.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderOptions { get; init; } =
        ProviderOptions ?? new Dictionary<string, string>();

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
}
