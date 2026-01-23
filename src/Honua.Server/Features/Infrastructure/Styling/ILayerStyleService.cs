// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Infrastructure.Styling;

/// <summary>
/// Service for resolving and updating layer styles.
/// </summary>
internal interface ILayerStyleService
{
    /// <summary>
    /// Retrieves the style snapshot for a layer.
    /// </summary>
    /// <param name="layer">Layer definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Style snapshot or null when not available.</returns>
    Task<LayerStyleSnapshot?> GetStyleAsync(LayerDefinition layer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves drawingInfo for a layer.
    /// </summary>
    /// <param name="layer">Layer definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>drawingInfo payload or null when not available.</returns>
    Task<JsonElement?> GetDrawingInfoAsync(LayerDefinition layer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the style for a layer using MapLibre or drawingInfo input.
    /// </summary>
    /// <param name="layer">Layer definition.</param>
    /// <param name="mapLibreStyle">MapLibre style payload.</param>
    /// <param name="drawingInfo">GeoServices drawingInfo payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Update result with status and style snapshot.</returns>
    Task<LayerStyleUpdateResult> UpdateStyleAsync(
        LayerDefinition layer,
        JsonElement? mapLibreStyle,
        JsonElement? drawingInfo,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Snapshot of stored style payloads.
/// </summary>
internal sealed record LayerStyleSnapshot(JsonElement? MapLibreStyle, JsonElement? DrawingInfo);

/// <summary>
/// Status for style updates.
/// </summary>
internal enum LayerStyleUpdateStatus
{
    /// <summary>
    /// Style update succeeded.
    /// </summary>
    Updated,
    /// <summary>
    /// Layer was not found.
    /// </summary>
    NotFound,
    /// <summary>
    /// Style payload was invalid.
    /// </summary>
    Invalid
}

/// <summary>
/// Result for style update attempts.
/// </summary>
internal sealed record LayerStyleUpdateResult(
    LayerStyleUpdateStatus Status,
    LayerStyleSnapshot? Style,
    string? ErrorMessage);
