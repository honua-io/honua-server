// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

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
    /// Valid values include "FeatureServer", "MapServer", "ImageServer", "OgcFeatures", "Wfs20", "Wms", "Wmts", "Wcs", "OData", "Grpc", "Stac", and "Terrain".
    /// </summary>
    public string[]? EnabledProtocols { get; init; }

    /// <summary>
    /// MapServer rendering configuration for this service. When null, defaults are used.
    /// </summary>
    public MapServerConfig? MapServer { get; init; }

    /// <summary>
    /// Optional STAC-specific metadata used when exposing the resource through the STAC API.
    /// </summary>
    public StacCatalogMetadata? Stac { get; init; }

    /// <summary>
    /// Optional raster mosaic defaults for layers backed by multiple rasters.
    /// </summary>
    public RasterMosaicSettings? RasterMosaic { get; init; }

    /// <summary>
    /// Optional extrusion configuration for 3D feature output.
    /// Null for 2D-only layers; the GeoServices FeatureServer layer
    /// response omits the <c>extrusionInfo</c> field entirely when
    /// this is null.
    /// </summary>
    public LayerExtrusionInfo? Extrusion { get; init; }

    /// <summary>
    /// Optional operator-managed filter that is always applied to public feature output.
    /// </summary>
    public LayerPermanentFilter? PermanentFilter { get; init; }
}

/// <summary>
/// Operator-managed layer filter persisted with layer metadata.
/// </summary>
public sealed record LayerPermanentFilter
{
    /// <summary>
    /// Filter expression text in the declared language.
    /// </summary>
    public required string Expression { get; init; }

    /// <summary>
    /// Filter language token. Supported values are <c>arcgis-sql</c>, <c>cql2-text</c>, and <c>cql2-json</c>.
    /// </summary>
    public string Language { get; init; } = LayerPermanentFilterLanguages.ArcGisSql;
}

/// <summary>
/// Stable layer permanent filter language tokens used by admin contracts.
/// </summary>
public static class LayerPermanentFilterLanguages
{
    /// <summary>
    /// GeoServices SQL where-clause syntax.
    /// </summary>
    public const string ArcGisSql = "arcgis-sql";

    /// <summary>
    /// OGC CQL2 text syntax.
    /// </summary>
    public const string Cql2Text = "cql2-text";

    /// <summary>
    /// OGC CQL2 JSON syntax.
    /// </summary>
    public const string Cql2Json = "cql2-json";
}

/// <summary>
/// Optional STAC metadata declared for a service or layer.
/// </summary>
public sealed record StacCatalogMetadata
{
    /// <summary>
    /// SPDX license identifier or STAC-recognized value such as <c>proprietary</c>.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// Keywords used to improve STAC collection discovery.
    /// </summary>
    public string[]? Keywords { get; init; }

    /// <summary>
    /// STAC extension URIs explicitly declared by the resource.
    /// </summary>
    public string[]? Extensions { get; init; }
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

/// <summary>
/// Layer-level defaults used when building a raster mosaic.
/// </summary>
public sealed record RasterMosaicSettings
{
    /// <summary>
    /// Default merge strategy applied to overlapping rasters.
    /// Valid values: newest, oldest, average, max, min.
    /// </summary>
    public string? MergeStrategy { get; init; }
}
