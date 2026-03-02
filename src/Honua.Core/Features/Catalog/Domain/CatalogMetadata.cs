// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Optional catalog metadata for services and layers.
/// </summary>
public sealed record CatalogMetadata
{
    /// <summary>
    /// Authorization policy for accessing the catalog resource.
    /// </summary>
    public AccessPolicy? AccessPolicy { get; init; }

    /// <summary>
    /// Temporal metadata for time-aware layers.
    /// </summary>
    public LayerTimeInfo? TimeInfo { get; init; }

    /// <summary>
    /// Protocols enabled for this service. When null, all protocols are enabled.
    /// Valid values: "FeatureServer", "MapServer", "OgcFeatures", "OData", "Grpc".
    /// </summary>
    public string[]? EnabledProtocols { get; init; }

    /// <summary>
    /// MapServer rendering configuration for this service. When null, defaults are used.
    /// </summary>
    public MapServerConfig? MapServer { get; init; }
}

/// <summary>
/// Per-service MapServer rendering configuration.
/// </summary>
/// <remarks>
/// Stored with service metadata and surfaced via admin APIs as part of the public domain model.
/// </remarks>
public sealed record MapServerConfig
{
    /// <summary>
    /// Maximum allowed image width in pixels.
    /// </summary>
    public int MaxImageWidth { get; init; } = 4096;

    /// <summary>
    /// Maximum allowed image height in pixels.
    /// </summary>
    public int MaxImageHeight { get; init; } = 4096;

    /// <summary>
    /// Default image width when not specified in the request.
    /// </summary>
    public int DefaultImageWidth { get; init; } = 400;

    /// <summary>
    /// Default image height when not specified in the request.
    /// </summary>
    public int DefaultImageHeight { get; init; } = 400;

    /// <summary>
    /// Default DPI for rendered map images.
    /// </summary>
    public int DefaultDpi { get; init; } = 96;

    /// <summary>
    /// Default output format (e.g. "png", "jpg").
    /// </summary>
    public string DefaultFormat { get; init; } = "png";

    /// <summary>
    /// Whether the background is transparent by default.
    /// </summary>
    public bool DefaultTransparent { get; init; } = true;

    /// <summary>
    /// Maximum number of features to render per layer.
    /// </summary>
    public int MaxFeaturesPerLayer { get; init; } = 10_000;
}

/// <summary>
/// Authorization policy for catalog resources (services/layers).
/// </summary>
public sealed record AccessPolicy
{
    /// <summary>
    /// When true, anonymous access is allowed regardless of other constraints.
    /// </summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// When true, anonymous write access is allowed regardless of other constraints.
    /// </summary>
    public bool AllowAnonymousWrite { get; init; }

    /// <summary>
    /// Allowed role names for access (case-insensitive).
    /// </summary>
    public string[]? AllowedRoles { get; init; }

    /// <summary>
    /// Allowed role names for write access (case-insensitive).
    /// Falls back to AllowedRoles when not specified.
    /// </summary>
    public string[]? AllowedWriteRoles { get; init; }
}

/// <summary>
/// Temporal metadata for layers with time awareness.
/// </summary>
public sealed record LayerTimeInfo
{
    /// <summary>
    /// Field name containing the start time (required when time info is present).
    /// </summary>
    public string? StartTimeField { get; init; }

    /// <summary>
    /// Field name containing the end time (optional for interval data).
    /// </summary>
    public string? EndTimeField { get; init; }

    /// <summary>
    /// Optional track identifier field for temporal visualization.
    /// </summary>
    public string? TrackIdField { get; init; }
}
