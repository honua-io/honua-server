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
    GeoJson,

    /// <summary>
    /// Shapefile format (.shp + .shx + .dbf)
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
    /// Comma-separated values (.csv)
    /// </summary>
    Csv,

    /// <summary>
    /// Tiny Well-Known Binary format (.twkb)
    /// </summary>
    TinyWkb,

    /// <summary>
    /// Esri File Geodatabase format (.gdb)
    /// </summary>
    FileGdb,

    /// <summary>
    /// FlatGeobuf format (.fgb) - compact binary geospatial format
    /// </summary>
    FlatGeobuf,

    /// <summary>
    /// GeoParquet format (.parquet, .geoparquet)
    /// </summary>
    GeoParquet
}
