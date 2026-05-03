// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Constants;

/// <summary>
/// Protocol identifiers for test trait attributes.
/// Used with <see cref="Attributes.ProtocolAttribute"/> for test categorization.
/// </summary>
public static class Protocols
{
    /// <summary>
    /// GeoServices REST API (Feature Server).
    /// </summary>
    public const string FeatureServer = "FeatureServer";

    /// <summary>
    /// GeoServices REST API (Map Server).
    /// </summary>
    public const string MapServer = "MapServer";

    /// <summary>
    /// OGC Web Map Service 1.3.0 operations.
    /// </summary>
    public const string Wms13 = "WMS-1.3.0";

    /// <summary>
    /// OGC Web Map Service 1.1.1 compatibility operations.
    /// </summary>
    public const string Wms111 = "WMS-1.1.1";

    /// <summary>
    /// OGC Web Map Tile Service 1.0.0 operations.
    /// </summary>
    public const string Wmts10 = "WMTS-1.0.0";

    /// <summary>
    /// OGC API - Features (Part 1: Core, Part 2: CRS, Part 3: Filtering).
    /// </summary>
    public const string OgcApiFeatures = "OGC-API-Features";

    /// <summary>
    /// OGC API - Processes (Part 1: Core).
    /// </summary>
    public const string OgcApiProcesses = "OGC-API-Processes";

    /// <summary>
    /// OGC API - Tiles (Part 1: Core).
    /// </summary>
    public const string OgcApiTiles = "OGC-API-Tiles";

    /// <summary>
    /// OData v4 protocol.
    /// </summary>
    public const string ODataV4 = "OData-v4";

    /// <summary>
    /// Mapbox Vector Tiles (MVT/PBF).
    /// </summary>
    public const string Mvt = "MVT";

    /// <summary>
    /// Terrain-RGB elevation tiles.
    /// </summary>
    public const string Terrain = "Terrain";

    /// <summary>
    /// Hosted OGC 3D Tiles scene serving (Cesium-compatible).
    /// </summary>
    public const string Scene = "Scene-3DTiles";

    /// <summary>
    /// Elevation query and profile API.
    /// </summary>
    public const string Elevation = "Elevation";

    /// <summary>
    /// Health and monitoring endpoints.
    /// </summary>
    public const string Health = "Health";

    /// <summary>
    /// Administrative API endpoints.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Cloud Optimized GeoTIFF registration and tile-serving endpoints.
    /// </summary>
    public const string Cog = "Cog";

    /// <summary>
    /// Infrastructure and cross-cutting concerns.
    /// </summary>
    public const string Infrastructure = "Infrastructure";

    /// <summary>
    /// Geometry Service utility operations.
    /// </summary>
    public const string GeometryService = "GeometryService";

    /// <summary>
    /// Comprehensive end-to-end coverage suites.
    /// </summary>
    public const string Comprehensive = "Comprehensive";

    /// <summary>
    /// Test quality validation suites.
    /// </summary>
    public const string TestQuality = "TestQuality";

    /// <summary>
    /// GeoServices Image Server REST API.
    /// </summary>
    public const string ImageServer = "ImageServer";

    /// <summary>
    /// OGC API - Maps (Part 1: Core).
    /// </summary>
    public const string OgcApiMaps = "OGC-API-Maps";

    /// <summary>
    /// OGC API - Coverages (Part 1: Core).
    /// </summary>
    public const string OgcApiCoverages = "OGC-API-Coverages";

    /// <summary>
    /// GeoServices REST service directory and root metadata endpoints.
    /// </summary>
    public const string GeoservicesCatalog = "GeoservicesCatalog";

    /// <summary>
    /// GeoServices GeocodeServer endpoints.
    /// </summary>
    public const string Geocoding = "Geocoding";

    /// <summary>
    /// gRPC/gRPC-Web protocol.
    /// </summary>
    public const string Grpc = "Grpc";

    /// <summary>
    /// OGC WFS 2.0 protocol.
    /// </summary>
    public const string Wfs20 = "WFS-2.0";

    /// <summary>
    /// OGC WCS 2.0.1 protocol.
    /// </summary>
    public const string Wcs201 = "WCS-2.0.1";

    /// <summary>
    /// OGC WFS 1.1.0 compatibility protocol.
    /// </summary>
    public const string Wfs11 = "WFS-1.1.0";

    /// <summary>
    /// OGC WFS 1.0.0 compatibility protocol.
    /// </summary>
    public const string Wfs10 = "WFS-1.0.0";

    /// <summary>
    /// Real-time feature-change streaming protocol (WebSocket/SSE).
    /// </summary>
    public const string Streaming = "Streaming";

    /// <summary>
    /// Static map image API.
    /// </summary>
    public const string StaticMap = "StaticMap";

    /// <summary>
    /// GeoServices PrintingTools (GP Server) endpoints.
    /// </summary>
    public const string PrintingTools = "PrintingTools";

    /// <summary>
    /// STAC (SpatioTemporal Asset Catalog) API.
    /// </summary>
    public const string Stac = "STAC";

    /// <summary>
    /// Pro-tier spatial analytics endpoints (clustering, spatial join, buffer
    /// aggregate, density binning). Mirrored under both REST and OGC route families.
    /// </summary>
    public const string SpatialAnalytics = "SpatialAnalytics";

    /// <summary>
    /// GeoServices GPServer REST API (geoprocessing tasks).
    /// </summary>
    public const string GPServer = "GPServer";

    /// <summary>
    /// Model Context Protocol operator surface (planning, execution, lifecycle).
    /// </summary>
    public const string Mcp = "Mcp";

    /// <summary>
    /// End-to-end operator workflow eval harness (analyst workflows,
    /// result packages, map/app outputs).
    /// </summary>
    public const string OperatorEval = "OperatorEval";

    /// <summary>
    /// Declarative spec plan/apply engine (HTTP /v1/spec/* and gRPC SpecService).
    /// </summary>
    public const string Spec = "Spec";

    /// <summary>
    /// Natural-language grounding and canonical spec synthesis endpoints.
    /// </summary>
    public const string Grounding = "Grounding";
}
