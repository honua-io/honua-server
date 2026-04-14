// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    GeoservicesFeatureService = 0,

    /// <summary>
    /// Esri GeoServices REST Map Service.
    /// </summary>
    GeoservicesMapService = 1,

    /// <summary>
    /// OGC API Features.
    /// </summary>
    OgcFeatures = 2,

    /// <summary>
    /// OGC API Maps.
    /// </summary>
    OgcMaps = 3,

    /// <summary>
    /// OGC API Tiles.
    /// </summary>
    OgcTiles = 4,

    /// <summary>
    /// OGC Web Feature Service.
    /// </summary>
    Wfs = 5,

    /// <summary>
    /// OGC Web Map Service.
    /// </summary>
    Wms = 6,

    /// <summary>
    /// OData v4 protocol.
    /// </summary>
    OData = 7,

    /// <summary>
    /// Vector tile source.
    /// </summary>
    VectorTile = 8,

    /// <summary>
    /// Raster tile source.
    /// </summary>
    RasterTile = 9,

    /// <summary>
    /// Artifact from a workspace.
    /// </summary>
    WorkspaceArtifact = 10
}
