// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Domain;

public enum PackageStatus
{
    Draft = 0,

    Composing = 1,

    Ready = 2,

    Failed = 3,

    Expired = 4
}

public enum SourceProtocol
{
    [JsonStringEnumMemberName("geoservices_feature_service")]
    GeoservicesFeatureService = 0,

    [JsonStringEnumMemberName("geoservices_map_service")]
    GeoservicesMapService = 1,

    [JsonStringEnumMemberName("ogc_features")]
    OgcFeatures = 2,

    [JsonStringEnumMemberName("ogc_maps")]
    OgcMaps = 3,

    [JsonStringEnumMemberName("ogc_tiles")]
    OgcTiles = 4,

    [JsonStringEnumMemberName("wfs")]
    Wfs = 5,

    [JsonStringEnumMemberName("wms")]
    Wms = 6,

    [JsonStringEnumMemberName("odata")]
    OData = 7,

    [JsonStringEnumMemberName("vector_tile")]
    VectorTile = 8,

    [JsonStringEnumMemberName("raster_tile")]
    RasterTile = 9,

    [JsonStringEnumMemberName("workspace_artifact")]
    WorkspaceArtifact = 10,

    [JsonStringEnumMemberName("pmtiles")]
    PMTiles = 11
}
