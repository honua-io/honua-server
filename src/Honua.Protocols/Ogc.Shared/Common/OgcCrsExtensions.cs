// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Common;

public static class OgcCrsExtensions
{
    public static int ToSrid(this string crs)
        => ExtentExtensions.TryExtractSridFromCrs(crs, out var srid)
            ? srid
            : throw new FormatException($"Unsupported CRS '{crs}'.");

    public static string ToOgcCrs(this int srid)
        => ExtentExtensions.ToOgcCrsUri(srid);

    public static string ToOgcCrs(this SpatialReference spatialRef)
        => ExtentExtensions.ToOgcCrsUri(spatialRef.ToSrid());

    public static SpatialReference ToSpatialReference(this string crs)
        => SpatialReference.Create(crs.ToSrid());
}
