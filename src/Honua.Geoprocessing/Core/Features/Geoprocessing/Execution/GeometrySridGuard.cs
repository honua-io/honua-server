// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

internal static class GeometrySridGuard
{
    public static bool TryValidateEmbeddedSrid(Geometry geometry, int declaredSrid, out string error)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.SRID > 0 && geometry.SRID != declaredSrid)
        {
            error = $"geometry carries embedded SRID {geometry.SRID} which does not match the declared srid {declaredSrid}";
            return false;
        }

        error = "";
        return true;
    }
}
