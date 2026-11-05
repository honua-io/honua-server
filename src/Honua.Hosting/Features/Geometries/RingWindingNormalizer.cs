// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Geometries;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Geometries;

// Stored clockwise exteriors violate the right-hand rule these formats require, so secondary emitters normalize instead of passing storage winding through.
internal static class RingWindingNormalizer
{
    // The GeoJSON writer is not thread-safe and must not be shared across calls.
    public static string WriteGeoJson(GeoJsonWriter writer, Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(geometry);
        return writer.Write(NormalizeToRightHandRule(geometry));
    }

    public static Geometry NormalizeToRightHandRule(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return RingOrientationNormalizer.Normalize(geometry, wantExteriorCcw: true);
    }
}
