// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Abstraction for persistence of layer styles.
/// </summary>
public interface ILayerStyleCatalog
{
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated style, or null when the layer does not exist.</returns>
    Task<LayerStyleDefinition?> SetMapLibreStyleAsync(int layerId, string mapLibreStyleJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores both MapLibre and drawingInfo styles for a layer.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="mapLibreStyleJson">MapLibre style JSON.</param>
    /// <param name="drawingInfoJson">GeoServices drawingInfo JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated style, or null when the layer does not exist.</returns>
    Task<LayerStyleDefinition?> SetStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string drawingInfoJson,
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
