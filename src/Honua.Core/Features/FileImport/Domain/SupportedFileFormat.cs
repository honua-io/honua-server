// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

[JsonConverter(typeof(JsonStringEnumConverter<SupportedFileFormat>))]
public enum SupportedFileFormat
{
    GeoJson = 0,

    Shapefile = 1,

    GeoPackage = 2,

    Gpx = 3,

    Kml = 4,

    Gml = 5,

    Wkt = 6,

    Csv = 7,

    FileGdb = 9,

    FlatGeobuf = 10,

    GeoParquet = 11,

    GeoServicesJson = 12,

    Wkb = 13
}
