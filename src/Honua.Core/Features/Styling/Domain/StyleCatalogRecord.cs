// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// A first-class, styleId-keyed style record from the independent style catalog
/// (ADR-0048, Phase 2). Decoupled from any single layer: one style may render many
/// layers through <see cref="StyleLayerAssociation"/> entries.
/// </summary>
public sealed record StyleCatalogRecord
{
    /// <summary>Stable string style identifier (<c>honua://styles/{StyleId}</c>).</summary>
    public required string StyleId { get; init; }

    /// <summary>Optional human-readable title.</summary>
    public string? Title { get; init; }

    /// <summary>Optional free-form description / abstract.</summary>
    public string? Description { get; init; }

    /// <summary>Canonical MapLibre/Mapbox style JSON.</summary>
    public required string MapLibreStyleJson { get; init; }

    /// <summary>Optional cached GeoServices drawingInfo JSON.</summary>
    public string? DrawingInfoJson { get; init; }

    /// <summary>Author-managed integer style version, incremented on canonical updates.</summary>
    public int StyleVersion { get; init; }

    /// <summary>UTC timestamp the style was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp of the last canonical update.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optional author or source identifier of the most recent revision.</summary>
    public string? RevisedBy { get; init; }

    /// <summary>Optional operator-supplied description of the most recent revision.</summary>
    public string? ChangeSummary { get; init; }
}

/// <summary>
/// An ordered association between a layer (integer storage-layer id) and a catalog
/// style. <see cref="Ordinal"/> <c>0</c> is the primary style, matching
/// <c>MetadataV2Resource.StyleResourceIds[0]</c>.
/// </summary>
/// <param name="LayerId">Integer storage-layer identifier.</param>
/// <param name="StyleId">Catalog style identifier.</param>
/// <param name="Ordinal">Zero-based reference ordinal; 0 is primary.</param>
public sealed record StyleLayerAssociation(int LayerId, string StyleId, int Ordinal);
