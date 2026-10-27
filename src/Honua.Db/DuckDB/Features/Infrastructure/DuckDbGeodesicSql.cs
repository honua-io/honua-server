// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.DuckDB.Features.Infrastructure;

/// <summary>
/// DuckDB geodesic-distance SQL. NAD27 is not measured as WGS 84 degrees: both
/// operands are transformed to EPSG:4326 first, which is the datum shift the
/// spatial extension can apply, and then measured with <c>ST_Distance_Spheroid</c>.
/// </summary>
internal static class DuckDbGeodesicSql
{
    /// <summary>EPSG:4267, NAD27. Not a WGS 84-compatible geographic CRS.</summary>
    public const int Nad27Srid = 4267;

    /// <summary>
    /// Spheroid distance in metres after transforming both NAD27 operands to EPSG:4326.
    /// <c>always_xy</c> keeps Honua's longitude/latitude axis order through the shift.
    /// </summary>
    public static string Nad27SpheroidDistance(string left, string right)
        => $"ST_Distance_Spheroid(ST_FlipCoordinates({ToWgs84(left)}), ST_FlipCoordinates({ToWgs84(right)}))";

    private static string ToWgs84(string geometry)
        => $"ST_Transform({geometry}, 'EPSG:4267', 'EPSG:4326', always_xy := true)";
}
