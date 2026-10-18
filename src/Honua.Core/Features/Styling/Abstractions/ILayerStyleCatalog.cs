// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Abstraction for persistence of layer styles.
/// </summary>
public interface ILayerStyleCatalog
{
    /// <summary>Invalidates any decorator cache after an atomic canonical multi-record edit.</summary>
    Task InvalidateStyleCacheAsync(int layerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Updates only when the current canonical version equals the expected version; null means no matching row.</summary>
    Task<LayerStyleDefinition?> SetMapLibreStyleIfVersionAsync(
        int layerId, string mapLibreStyleJson, int expectedVersion,
        string? revisedBy = null, string? changeSummary = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Conditional style updates are unavailable.");

    /// <summary>Updates only when the current canonical version equals the expected version; null means no matching row.</summary>
    Task<LayerStyleDefinition?> SetStyleIfVersionAsync(
        int layerId, string mapLibreStyleJson, string drawingInfoJson, int expectedVersion,
        string? revisedBy = null, string? changeSummary = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Conditional style updates are unavailable.");

    /// <summary>
    /// Retrieves the stored style for a layer.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stored style, or null when not found.</returns>
    Task<LayerStyleDefinition?> GetLayerStyleAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a MapLibre style for a layer and clears cached drawingInfo.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="mapLibreStyleJson">MapLibre style JSON.</param>
    /// <param name="revisedBy">Optional author or source identifier captured for the new revision.</param>
    /// <param name="changeSummary">Optional operator-supplied description of the change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated style, or null when the layer does not exist.</returns>
    Task<LayerStyleDefinition?> SetMapLibreStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores both MapLibre and drawingInfo styles for a layer.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="mapLibreStyleJson">MapLibre style JSON.</param>
    /// <param name="drawingInfoJson">GeoServices drawingInfo JSON.</param>
    /// <param name="revisedBy">Optional author or source identifier captured for the new revision.</param>
    /// <param name="changeSummary">Optional operator-supplied description of the change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated style, or null when the layer does not exist.</returns>
    Task<LayerStyleDefinition?> SetStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string drawingInfoJson,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores GeoServices drawingInfo for a layer without changing canonical style.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="drawingInfoJson">GeoServices drawingInfo JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated style, or null when the layer does not exist.</returns>
    Task<LayerStyleDefinition?> SetDrawingInfoAsync(int layerId, string drawingInfoJson, CancellationToken cancellationToken = default);
}
