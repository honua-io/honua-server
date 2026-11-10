// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Raster.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<SupportedRasterFormat>))]
public enum SupportedRasterFormat
{
    GeoTiff,

    PngWorldFile,

    JpegWorldFile,

    CloudOptimizedGeoTiff,

    Zarr
}
