// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Supported vector file formats for import using NetTopologySuite
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportedFileFormat
{
    /// <summary>
    /// GeoJSON format (.geojson, .json)
    /// </summary>
    GeoJson,

    /// <summary>
    /// Esri Shapefile format (.shp + .shx + .dbf)
    /// </summary>
    Shapefile,

    /// <summary>
    /// OGC GeoPackage format (.gpkg)
    /// </summary>
    GeoPackage,

    /// <summary>
    /// GPS Exchange format (.gpx)
    /// </summary>
    Gpx,

    /// <summary>
    /// Keyhole Markup Language (.kml, .kmz)
    /// </summary>
    Kml,

    /// <summary>
    /// Geography Markup Language (.gml)
    /// </summary>
    Gml,

    /// <summary>
    /// Well-Known Text format (.wkt)
    /// </summary>
    Wkt,

    /// <summary>
    /// Tiny Well-Known Binary format (.twkb)
    /// </summary>
    TinyWkb
}
