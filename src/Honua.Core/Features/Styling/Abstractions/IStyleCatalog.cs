// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

// Catalog styles are keyed by a stable style id and can render many layers, unlike the one-style-per-storage-layer store.
public interface IStyleCatalog
{
    Task<StyleCatalogRecord?> GetStyleAsync(string styleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StyleCatalogRecord>> ListStylesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StyleCatalogRecord>> GetStylesForLayerAsync(int layerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StyleLayerAssociation>> ListAssociationsAsync(CancellationToken cancellationToken = default);

    Task<StyleCatalogRecord?> CreateStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        string? title = null,
        string? description = null,
        string? drawingInfoJson = null,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    Task<StyleCatalogRecord> UpsertStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        string? title = null,
        string? description = null,
        string? drawingInfoJson = null,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    Task<StyleCatalogRecord?> UpdateStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        string? title = null,
        string? description = null,
        string? drawingInfoJson = null,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    Task<StyleCatalogDeleteResult> DeleteStyleAsync(
        string styleId,
        int? protectedLayerId = null,
        CancellationToken cancellationToken = default);

    Task<bool> AssociateLayerAsync(int layerId, string styleId, int ordinal = 0, CancellationToken cancellationToken = default);
}
