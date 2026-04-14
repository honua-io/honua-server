// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Lifecycle status for a composed package (map or app).
/// </summary>
public enum PackageStatus
{
    /// <summary>
    /// Package created but composition has not started.
    /// </summary>
    Draft = 0,

    /// <summary>
    /// Package is being composed from workflow outputs.
    /// </summary>
    Composing = 1,

    /// <summary>
    /// Package composition completed and the package is ready for consumption.
    /// </summary>
    Ready = 2,

    /// <summary>
    /// Package composition failed.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Package has expired per retention policy.
    /// </summary>
    Expired = 4
}

/// <summary>
/// Data source protocol for a package source binding.
/// </summary>
public enum SourceProtocol
{
    /// <summary>
    /// Esri GeoServices REST Feature Service.
    /// </summary>
    [JsonStringEnumMemberName("geoservices_feature_service")]
    GeoservicesFeatureService = 0,

    /// <summary>
    /// Esri GeoServices REST Map Service.
    /// </summary>
    [JsonStringEnumMemberName("geoservices_map_service")]
    GeoservicesMapService = 1,

    /// <summary>
    /// OGC API Features.
    /// </summary>
    [JsonStringEnumMemberName("ogc_features")]
    OgcFeatures = 2,

    /// <summary>
    /// OGC API Maps.
    /// </summary>
    [JsonStringEnumMemberName("ogc_maps")]
    OgcMaps = 3,

    /// <summary>
    /// OGC API Tiles.
    /// </summary>
    [JsonStringEnumMemberName("ogc_tiles")]
    OgcTiles = 4,

    /// <summary>
    /// OGC Web Feature Service.
    /// </summary>
    [JsonStringEnumMemberName("wfs")]
    Wfs = 5,

    /// <summary>
    /// OGC Web Map Service.
    /// </summary>
    [JsonStringEnumMemberName("wms")]
    Wms = 6,

    /// <summary>
    /// OData v4 protocol.
    /// </summary>
    [JsonStringEnumMemberName("odata")]
    OData = 7,

    /// <summary>
    /// Vector tile source.
    /// </summary>
    [JsonStringEnumMemberName("vector_tile")]
    VectorTile = 8,

    /// <summary>
    /// Raster tile source.
    /// </summary>
    [JsonStringEnumMemberName("raster_tile")]
    RasterTile = 9,

    /// <summary>
    /// Artifact from a workspace.
    /// </summary>
    [JsonStringEnumMemberName("workspace_artifact")]
    WorkspaceArtifact = 10
}
