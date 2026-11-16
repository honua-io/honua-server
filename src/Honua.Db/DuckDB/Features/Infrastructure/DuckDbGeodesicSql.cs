// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.DuckDB.Features.Infrastructure;

internal static class DuckDbGeodesicSql
{
    public const int Nad27Srid = 4267;

    public static string Nad27SpheroidDistance(string left, string right)
        => $"ST_Distance_Spheroid(ST_FlipCoordinates({ToWgs84(left)}), ST_FlipCoordinates({ToWgs84(right)}))";

    private static string ToWgs84(string geometry)
        => $"ST_Transform({geometry}, 'EPSG:4267', 'EPSG:4326', always_xy := true)";
}
