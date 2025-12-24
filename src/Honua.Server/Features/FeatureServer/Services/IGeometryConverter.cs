// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Service for converting between geometry formats
/// </summary>
internal interface IGeometryConverter
{
    /// <summary>
    /// Converts GeoServices JSON geometry to Well-Known Binary (WKB) format
    /// </summary>
    /// <param name="geoServicesJsonGeometry">Geometry in GeoServices JSON format</param>
    /// <returns>Geometry in WKB format</returns>
    /// <exception cref="ArgumentException">Thrown when geometry format is invalid</exception>
    byte[] ConvertGeoServicesJsonToWkb(string geoServicesJsonGeometry);

    /// <summary>
    /// Converts Well-Known Binary (WKB) geometry to GeoJSON format
    /// </summary>
    /// <param name="wkbGeometry">Geometry in WKB format</param>
    /// <returns>Geometry in GeoJSON format as a JSON object</returns>
    /// <exception cref="ArgumentException">Thrown when WKB format is invalid</exception>
    object? ConvertWkbToGeoJson(byte[] wkbGeometry);
}
