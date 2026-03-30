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
    /// OGC API - Features (Part 1: Core, Part 2: CRS, Part 3: Filtering).
    /// </summary>
    public const string OgcApiFeatures = "OGC-API-Features";

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
    /// Health and monitoring endpoints.
    /// </summary>
    public const string Health = "Health";

    /// <summary>
    /// Administrative API endpoints.
    /// </summary>
    public const string Admin = "Admin";

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
}
