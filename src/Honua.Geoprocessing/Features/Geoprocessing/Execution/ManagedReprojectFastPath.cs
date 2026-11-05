// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Geoprocessing.Execution;

internal static class ManagedReprojectFastPath
{
    public static bool IsManagedFastPath(int fromSrid, int toSrid)
    {
        if (fromSrid == toSrid)
        {
            return true;
        }

        if (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
        {
            return true;
        }

        if (fromSrid == 4326 && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
        {
            return true;
        }

        return SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && toSrid == 4326;
    }

    public static bool RequiresNativeWorker(int fromSrid, int toSrid)
        => !IsManagedFastPath(fromSrid, toSrid);

    // A Web-Mercator alias pair is the same projected space and copies through, while 4326 to Web Mercator still transforms.
    public static bool IsPassthrough(int fromSrid, int toSrid)
        => fromSrid == toSrid
            || (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid));

    public static bool TryParseSrid(string? raw, out int srid)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid) || srid <= 0)
        {
            srid = 0;
            return false;
        }

        return true;
    }
}
