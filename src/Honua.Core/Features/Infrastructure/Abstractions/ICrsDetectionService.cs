// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Service for detecting coordinate reference systems from various sources
/// </summary>
public interface ICrsDetectionService
{
    /// <summary>
    /// Detect CRS from a .prj file content (shapefile projection file format)
    /// </summary>
    /// <param name="prjContent">Content of a .prj file</param>
    /// <returns>Detected SRID or null if not recognized</returns>
    Task<int?> DetectFromPrjAsync(string prjContent);

    /// <summary>
    /// Detect CRS from Well-Known Text string
    /// </summary>
    /// <param name="wktContent">WKT string representation</param>
    /// <returns>Detected SRID or null if not recognized</returns>
    Task<int?> DetectFromWktAsync(string wktContent);

    /// <summary>
    /// Detect CRS from EPSG code
    /// </summary>
    /// <param name="epsgCode">EPSG code (e.g., "EPSG:4326" or "4326")</param>
    /// <returns>Parsed SRID or null if invalid</returns>
    int? DetectFromEpsgCode(string epsgCode);

    /// <summary>
    /// Detect CRS from GeoJSON CRS object
    /// </summary>
    /// <param name="crsObject">GeoJSON CRS object as JSON</param>
    /// <returns>Detected SRID or null if not recognized</returns>
    Task<int?> DetectFromGeoJsonCrsAsync(string crsObject);

    /// <summary>
    /// Detect CRS from shapefile by looking for accompanying .prj file
    /// </summary>
    /// <param name="shapefilePath">Path to the .shp file</param>
    /// <returns>Detected SRID or null if no .prj file found or not recognized</returns>
    Task<int?> DetectFromShapefilePrjAsync(string shapefilePath);

    /// <summary>
    /// Validate that an SRID exists in the spatial reference system database
    /// </summary>
    /// <param name="srid">SRID to validate</param>
    /// <returns>True if SRID exists in the database</returns>
    Task<bool> ValidateSridAsync(int srid);
}
