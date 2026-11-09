// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class ServiceProtocols
{
    public const string FeatureServer = "FeatureServer";

    public const string MapServer = "MapServer";

    public const string ImageServer = "ImageServer";

    public const string GPServer = "GPServer";

    public const string VectorTileServer = "VectorTileServer";

    public const string OgcFeatures = "OgcFeatures";

    public const string OgcApiMaps = "OGC-API-Maps";

    public const string OgcApiCoverages = "OGC-API-Coverages";

    public const string OgcApiTiles = "OGC-API-Tiles";

    public const string Wfs20 = "Wfs20";

    public const string Wms = "Wms";

    public const string Wmts = "Wmts";

    public const string Wcs = "Wcs";

    public const string OData = "OData";

    public const string Grpc = "Grpc";

    public const string Stac = "Stac";

    public const string SensorThings = "SensorThings";

    public const string Terrain = "Terrain";

    public const string Elevation = "Elevation";

    public static readonly string[] All =
    [
        FeatureServer,
        MapServer,
        ImageServer,
        GPServer,
        VectorTileServer,
        OgcFeatures,
        OgcApiMaps,
        OgcApiCoverages,
        OgcApiTiles,
        Wfs20,
        Wms,
        Wmts,
        Wcs,
        OData,
        Grpc,
        Stac,
        SensorThings,
        Terrain,
        Elevation
    ];

    public static bool IsProtocolEnabled(MetadataV2Service? service, string protocol)
    {
        if (service is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(protocol))
        {
            return true;
        }

        return service.Protocols.Any(enabled => string.Equals(enabled, protocol, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPreferredPublicationType(string? protocol, MetadataV2LayerType publicationType)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return false;
        }

        if (string.Equals(protocol, OgcFeatures, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.OgcCollection;
        }

        if (string.Equals(protocol, Stac, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.StacCollection;
        }

        if (string.Equals(protocol, Wfs20, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.WfsFeatureType;
        }

        if (string.Equals(protocol, FeatureServer, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.EsriFeatureLayer;
        }

        if (string.Equals(protocol, MapServer, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.EsriMapLayer;
        }

        if (string.Equals(protocol, ImageServer, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.EsriImageLayer;
        }

        if (string.Equals(protocol, VectorTileServer, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.EsriVectorTileLayer;
        }

        if (string.Equals(protocol, OData, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.ODataEntitySet;
        }

        if (string.Equals(protocol, SensorThings, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.SensorThingsDatastream;
        }

        if (string.Equals(protocol, Wms, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.WmsLayer;
        }

        if (string.Equals(protocol, Wmts, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protocol, OgcApiTiles, StringComparison.OrdinalIgnoreCase))
        {
            return publicationType == MetadataV2LayerType.WmtsLayer;
        }

        return false;
    }
}
