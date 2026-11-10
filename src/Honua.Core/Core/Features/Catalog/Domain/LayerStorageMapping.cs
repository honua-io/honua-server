// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Catalog.Domain;

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
    public const string SourceBackedOption = "sourceBacked";

    public IReadOnlyDictionary<string, string> ProviderOptions { get; init; } =
        ProviderOptions ?? new Dictionary<string, string>();

    public bool IsSourceBacked =>
        ProviderOptions.TryGetValue(SourceBackedOption, out var value)
        && bool.TryParse(value, out var sourceBacked)
        && sourceBacked;

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
