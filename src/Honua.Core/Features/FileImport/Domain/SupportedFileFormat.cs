// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Domain;

/// <summary>
/// Supported vector file formats for import using NetTopologySuite
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SupportedFileFormat>))]
public enum SupportedFileFormat
{
    /// <summary>
    /// GeoJSON format (.geojson, .json)
    /// </summary>
    GeoJson = 0,

    /// <summary>
    /// Shapefile format (.shp + .shx + .dbf)
    /// </summary>
    Shapefile = 1,

    /// <summary>
    /// OGC GeoPackage format (.gpkg)
    /// </summary>
    GeoPackage = 2,

    /// <summary>
    /// GPS Exchange format (.gpx)
    /// </summary>
    Gpx = 3,

    /// <summary>
    /// Keyhole Markup Language (.kml, .kmz)
    /// </summary>
    Kml = 4,

    /// <summary>
    /// Geography Markup Language (.gml)
    /// </summary>
    Gml = 5,

    /// <summary>
    /// Well-Known Text format (.wkt)
    /// </summary>
    Wkt = 6,

    /// <summary>
    /// Comma-separated values (.csv)
    /// </summary>
    Csv = 7,

    /// <summary>
    /// Esri File Geodatabase format (.gdb)
    /// </summary>
    FileGdb = 9,

    /// <summary>
    /// FlatGeobuf format (.fgb) - compact binary geospatial format
    /// </summary>
    FlatGeobuf = 10,

    /// <summary>
    /// GeoParquet format (.parquet, .geoparquet)
    /// </summary>
    GeoParquet = 11,

    /// <summary>
    /// Esri JSON feature set (.esrijson) — features with an <c>attributes</c> object and an
    /// Esri geometry (<c>x</c>/<c>y</c>, <c>points</c>, <c>paths</c>, or <c>rings</c>).
    /// </summary>
    EsriJson = 12,

    /// <summary>
    /// Well-Known Binary geometry (.wkb) — one or more concatenated WKB/EWKB geometries.
    /// </summary>
    Wkb = 13
}
