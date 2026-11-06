// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

public sealed record StyleCatalogRecord
{
    public required string StyleId { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public required string MapLibreStyleJson { get; init; }

    public string? DrawingInfoJson { get; init; }

    public int StyleVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? RevisedBy { get; init; }

    public string? ChangeSummary { get; init; }
}

public sealed record StyleLayerAssociation(int LayerId, string StyleId, int Ordinal);

public sealed record StyleCatalogDeleteResult(
    StyleCatalogDeleteStatus Status,
    IReadOnlyList<int> AssociatedLayerIds);

public enum StyleCatalogDeleteStatus
{
    Deleted,

    NotFound,

    Protected
}
