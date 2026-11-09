// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Services;

internal interface IGeometryConverter
{
    byte[] ConvertGeoServicesJsonToWkb(string geoServicesJsonGeometry);

    object? ConvertWkbToGeoJson(byte[] wkbGeometry);

    object? ConvertWkbToGeoJson(Memory<byte> wkbGeometry);

    System.Text.Json.JsonElement ConvertWkbToGeoServicesGeometry(byte[] wkbGeometry, int srid);
}
