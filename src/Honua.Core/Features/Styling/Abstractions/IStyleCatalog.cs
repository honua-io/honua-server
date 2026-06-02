// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Independent, styleId-keyed style store (ADR-0048, Phase 2, issue #1389).
/// Unlike <see cref="ILayerStyleCatalog"/> — which keys style bytes by an integer
/// storage-layer id and binds one style to exactly one layer — this catalog stores
/// styles by a stable string <c>styleId</c> decoupled from any layer, and maintains an
/// ordered many-to-many association so one style can render many layers. It is the
/// backing store for the OGC API - Styles <c>manage-styles</c> create/delete surface and
/// for the <c>Type=Style</c> metadata-v2 graph producer.
/// </summary>
public interface IStyleCatalog
{
    /// <summary>
    /// Gets a single catalog style by its stable identifier.
    /// </summary>
    /// <param name="styleId">Stable style identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The style, or <c>null</c> when no such style exists.</returns>
    Task<StyleCatalogRecord?> GetStyleAsync(string styleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every catalog style, ordered by style identifier.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All catalog styles.</returns>
    Task<IReadOnlyList<StyleCatalogRecord>> ListStylesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the catalog styles associated with a layer, ordered by ordinal
    /// (<c>0</c> = primary).
    /// </summary>
    /// <param name="layerId">Integer storage-layer identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The associated styles, primary first.</returns>
    Task<IReadOnlyList<StyleCatalogRecord>> GetStylesForLayerAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every layer/style association in the catalog. Used by the graph producer
    /// to populate <c>StyleResourceIds</c> across all data resources.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All associations.</returns>
    Task<IReadOnlyList<StyleLayerAssociation>> ListAssociationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new standalone catalog style. Fails when a style with the same
    /// identifier already exists.
    /// </summary>
    /// <param name="styleId">Stable style identifier.</param>
    /// <param name="mapLibreStyleJson">Canonical MapLibre style JSON.</param>
    /// <param name="title">Optional title.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="drawingInfoJson">Optional cached GeoServices drawingInfo JSON.</param>
    /// <param name="revisedBy">Optional author or source identifier.</param>
    /// <param name="changeSummary">Optional change summary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created style, or <c>null</c> when the identifier already exists.</returns>
    Task<StyleCatalogRecord?> CreateStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        string? title = null,
        string? description = null,
        string? drawingInfoJson = null,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates a catalog style (upsert by <c>styleId</c>). On update the
    /// canonical MapLibre style is replaced and the version is incremented.
    /// </summary>
    /// <param name="styleId">Stable style identifier.</param>
    /// <param name="mapLibreStyleJson">Canonical MapLibre style JSON.</param>
    /// <param name="title">Optional title.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="drawingInfoJson">Optional cached GeoServices drawingInfo JSON.</param>
    /// <param name="revisedBy">Optional author or source identifier.</param>
    /// <param name="changeSummary">Optional change summary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upserted style.</returns>
    Task<StyleCatalogRecord> UpsertStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        string? title = null,
        string? description = null,
        string? drawingInfoJson = null,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a catalog style and all of its layer associations.
    /// </summary>
    /// <param name="styleId">Stable style identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a style was deleted; <c>false</c> when none existed.</returns>
    Task<bool> DeleteStyleAsync(string styleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Associates a style with a layer at the given ordinal (idempotent upsert of the
    /// association). Used so one style can render many layers.
    /// </summary>
    /// <param name="layerId">Integer storage-layer identifier.</param>
    /// <param name="styleId">Stable style identifier.</param>
    /// <param name="ordinal">Zero-based reference ordinal; 0 is primary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the association was applied; <c>false</c> when the style or layer does not exist.</returns>
    Task<bool> AssociateLayerAsync(int layerId, string styleId, int ordinal = 0, CancellationToken cancellationToken = default);
}
