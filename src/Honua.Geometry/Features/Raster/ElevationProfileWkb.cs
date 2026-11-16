// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Features.Raster.Geometry;

public static class ElevationProfileWkb
{
    public static byte[] Write(LineString lineString)
    {
        ArgumentNullException.ThrowIfNull(lineString);
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false);
        return writer.Write(lineString);
    }
}
