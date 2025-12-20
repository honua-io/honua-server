// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Service for converting between geometry formats
/// </summary>
internal interface IGeometryConverter
{
    /// <summary>
    /// Converts Esri JSON geometry to Well-Known Binary (WKB) format
    /// </summary>
    /// <param name="esriJsonGeometry">Geometry in Esri JSON format</param>
    /// <returns>Geometry in WKB format</returns>
    /// <exception cref="ArgumentException">Thrown when geometry format is invalid</exception>
    byte[] ConvertEsriJsonToWkb(string esriJsonGeometry);
}
