// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Supported raster file formats for import.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SupportedRasterFormat>))]
public enum SupportedRasterFormat
{
    /// <summary>
    /// GeoTIFF format (.tif, .tiff) with embedded georeferencing.
    /// </summary>
    GeoTiff,

    /// <summary>
    /// PNG with world file (.png + .pgw) and optional .prj.
    /// </summary>
    PngWorldFile,

    /// <summary>
    /// JPEG with world file (.jpg/.jpeg + .jgw) and optional .prj.
    /// </summary>
    JpegWorldFile,

    /// <summary>
    /// Cloud-Optimized GeoTIFF (.tif, .tiff) with internal tiling and overview IFDs.
    /// </summary>
    CloudOptimizedGeoTiff,

    /// <summary>
    /// Zarr v2 store hosted locally or in object storage. Read-only.
    /// </summary>
    Zarr
}
