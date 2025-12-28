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
    /// OGC API - Features (Part 1: Core, Part 2: CRS, Part 3: Filtering).
    /// </summary>
    public const string OgcApiFeatures = "OGC-API-Features";

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
    /// Comprehensive end-to-end coverage suites.
    /// </summary>
    public const string Comprehensive = "Comprehensive";

    /// <summary>
    /// Test quality validation suites.
    /// </summary>
    public const string TestQuality = "TestQuality";
}
