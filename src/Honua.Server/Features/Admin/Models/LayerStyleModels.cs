// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request payload for updating a layer style.
/// </summary>
public sealed class LayerStyleUpdateRequest
{
    /// <summary>
    /// MapLibre style JSON (canonical format).
    /// </summary>
    public JsonElement? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo JSON (import/compat).
    /// </summary>
    public JsonElement? DrawingInfo { get; init; }
}

/// <summary>
/// Response payload containing layer styles.
/// </summary>
public sealed class LayerStyleResponse
{
    /// <summary>
    /// MapLibre style JSON (canonical format).
    /// </summary>
    public JsonElement? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo JSON (cached conversion or import).
    /// </summary>
    public JsonElement? DrawingInfo { get; init; }
}
