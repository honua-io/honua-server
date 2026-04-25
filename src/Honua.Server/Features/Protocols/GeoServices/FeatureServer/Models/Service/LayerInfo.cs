// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Basic layer info for service listing
/// </summary>
public sealed class LayerInfo
{
    /// <summary>
    /// Layer identifier
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Layer name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Parent layer ID (for group layers)
    /// </summary>
    public int? ParentLayerId { get; init; }

    /// <summary>
    /// Default visibility state
    /// </summary>
    public bool DefaultVisibility { get; init; } = true;

    /// <summary>
    /// Sub-layer IDs (for group layers)
    /// </summary>
    public int[]? SubLayerIds { get; init; }

    /// <summary>
    /// Minimum scale for visibility
    /// </summary>
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum scale for visibility
    /// </summary>
    public double? MaxScale { get; init; }

    /// <summary>
    /// Layer type
    /// </summary>
    public string Type { get; init; } = "Feature Layer";

    /// <summary>
    /// Geometry type
    /// </summary>
    public required string GeometryType { get; init; }
}
