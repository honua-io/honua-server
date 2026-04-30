// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Constants and helpers for service protocol identifiers.
/// </summary>
public static class ServiceProtocols
{
    /// <summary>GeoServices FeatureServer protocol.</summary>
    public const string FeatureServer = "FeatureServer";

    /// <summary>GeoServices MapServer protocol.</summary>
    public const string MapServer = "MapServer";

    /// <summary>GeoServices ImageServer protocol.</summary>
    public const string ImageServer = "ImageServer";

    /// <summary>GeoServices GPServer protocol.</summary>
    public const string GPServer = "GPServer";

    /// <summary>OGC API Features protocol.</summary>
    public const string OgcFeatures = "OgcFeatures";

    /// <summary>OGC API Maps protocol.</summary>
    public const string OgcApiMaps = "OGC-API-Maps";

    /// <summary>OGC API Tiles protocol.</summary>
    public const string OgcApiTiles = "OGC-API-Tiles";

    /// <summary>OGC Web Feature Service 2.0 protocol.</summary>
    public const string Wfs20 = "Wfs20";

    /// <summary>OGC Web Map Service protocol.</summary>
    public const string Wms = "Wms";

    /// <summary>OGC Web Map Tile Service protocol.</summary>
    public const string Wmts = "Wmts";

    /// <summary>OGC Web Coverage Service 2.0 protocol.</summary>
    public const string Wcs = "Wcs";

    /// <summary>OData v4 protocol.</summary>
    public const string OData = "OData";

    /// <summary>gRPC/gRPC-Web protocol.</summary>
    public const string Grpc = "Grpc";

    /// <summary>STAC (SpatioTemporal Asset Catalog) protocol.</summary>
    public const string Stac = "Stac";

    /// <summary>
    /// All supported protocol identifiers.
    /// </summary>
    public static readonly string[] All =
    [
        FeatureServer,
        MapServer,
        ImageServer,
        GPServer,
        OgcFeatures,
        OgcApiMaps,
        OgcApiTiles,
        Wfs20,
        Wms,
        Wmts,
        Wcs,
        OData,
        Grpc,
        Stac
    ];

    /// <summary>
    /// Checks whether a protocol is enabled for a service based on its metadata.
    /// When <see cref="CatalogMetadata.EnabledProtocols"/> is null, all protocols are enabled.
    /// </summary>
    public static bool IsProtocolEnabled(CatalogMetadata? metadata, string protocol)
        => metadata?.EnabledProtocols is null || metadata.EnabledProtocols.Contains(protocol);
}
