// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

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

    /// <summary>OGC API Coverages protocol.</summary>
    public const string OgcApiCoverages = "OGC-API-Coverages";

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

    /// <summary>Terrain-RGB elevation tile protocol.</summary>
    public const string Terrain = "Terrain";

    /// <summary>Elevation query and profile protocol.</summary>
    public const string Elevation = "Elevation";

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
        OgcApiCoverages,
        OgcApiTiles,
        Wfs20,
        Wms,
        Wmts,
        Wcs,
        OData,
        Grpc,
        Stac,
        Terrain,
        Elevation
    ];

    /// <summary>
    /// Checks whether a protocol is enabled for a service based on its metadata.
    /// When <see cref="CatalogMetadata.EnabledProtocols"/> is null, all protocols are enabled.
    /// </summary>
    public static bool IsProtocolEnabled(CatalogMetadata? metadata, string protocol)
        => metadata?.EnabledProtocols is null || metadata.EnabledProtocols.Contains(protocol);

    /// <summary>
    /// Checks whether a V2 service has <paramref name="protocol"/> enabled. The lookup
    /// order is:
    /// <list type="number">
    /// <item><see cref="MetadataV2Service.EnabledProtocols"/> — explicit set; if non-null,
    /// only protocols listed here are enabled.</item>
    /// <item><see cref="MetadataV2Service.ServiceType"/> — when the protocol maps onto a
    /// known V2 <see cref="MetadataV2ServiceType"/>, the service-type itself implies the
    /// canonical protocol identifier (e.g. <see cref="MetadataV2ServiceType.OgcApiFeatures"/>
    /// implies <c>OgcFeatures</c>).</item>
    /// </list>
    /// Returns true when no protocol is specified.
    /// </summary>
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

        // Explicit enablement set wins. Mirrors the v1 EnabledProtocols semantics: when the
        // operator pinned a specific set, only those are enabled.
        if (service.EnabledProtocols is not null)
        {
            foreach (var enabled in service.EnabledProtocols)
            {
                if (string.Equals(enabled, protocol, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // Fall back to the implicit service-type → protocol mapping.
        var requested = MetadataV2ServiceTypeMapping.Map(protocol);
        return requested.HasValue && service.ServiceType == requested.Value;
    }
}
