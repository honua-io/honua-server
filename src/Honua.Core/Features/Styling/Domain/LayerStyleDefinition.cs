// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Represents stored style metadata for a layer.
/// </summary>
public sealed record LayerStyleDefinition
{
    /// <summary>
    /// Layer identifier that owns this style.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// MapLibre style JSON (canonical form).
    /// </summary>
    public string? MapLibreStyleJson { get; init; }

    /// <summary>
    /// GeoServices drawingInfo JSON (cached conversion or import).
    /// </summary>
    public string? DrawingInfoJson { get; init; }

    /// <summary>
    /// Version counter incremented on canonical style updates.
    /// </summary>
    public int StyleVersion { get; init; }
}
