// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Sdk.Admin.Models;

/// <summary>
/// Response payload containing layer styles.
/// </summary>
public sealed class LayerStyleResponse
{
    /// <summary>
    /// MapLibre style JSON (canonical format).
    /// </summary>
    [JsonPropertyName("mapLibreStyle")]
    public JsonElement? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo JSON (cached conversion or import).
    /// </summary>
    [JsonPropertyName("drawingInfo")]
    public JsonElement? DrawingInfo { get; init; }
}

/// <summary>
/// Request payload for updating a layer style.
/// </summary>
public sealed class LayerStyleUpdateRequest
{
    /// <summary>
    /// MapLibre style JSON (canonical format).
    /// </summary>
    [JsonPropertyName("mapLibreStyle")]
    public JsonElement? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo JSON (import/compat).
    /// </summary>
    [JsonPropertyName("drawingInfo")]
    public JsonElement? DrawingInfo { get; init; }
}
