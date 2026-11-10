// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.EnrichmentCatalog.Domain;

public sealed record EnrichmentDatasetRecord(
    string Id,
    string Title,
    string Category,
    int LayerId,
    string? GeometryType,
    IReadOnlyList<string> JoinAttributes,
    string DefaultPredicate,
    double? DistanceMeters,
    string? Provenance,
    string? Attribution,
    string? License,
    HonuaEdition MinimumEdition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy);

public static class EnrichmentDatasetCategories
{
    public const string Boundary = "boundary";

    public const string Demographic = "demographic";

    public const string Poi = "poi";

    public static bool IsValid(string? value) =>
        string.Equals(value, Boundary, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Demographic, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Poi, StringComparison.OrdinalIgnoreCase);
}

public sealed class EnrichmentDatasetAlreadyExistsException : Exception
{
    public EnrichmentDatasetAlreadyExistsException(string datasetId)
        : base($"An enrichment dataset with id '{datasetId}' is already registered.")
        => DatasetId = datasetId;

    public string DatasetId { get; }
}
