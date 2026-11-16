// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Db.MySql.Features.Infrastructure;

internal static class MySqlSpatialSql
{
    private const string MysqlAxisOrderOption = "'axis-order=long-lat'";

    public static string AsWkb(string quotedColumn, MySqlEngineFlavor flavor) =>
        flavor == MySqlEngineFlavor.Mysql
            ? $"ST_AsWKB({quotedColumn}, {MysqlAxisOrderOption})"
            : $"ST_AsWKB({quotedColumn})";

    public static string GeomFromWkb(string wkbParam, int srid, MySqlEngineFlavor flavor)
    {
        var sridLiteral = srid.ToString(CultureInfo.InvariantCulture);
        return flavor == MySqlEngineFlavor.Mysql
            ? $"ST_GeomFromWKB({wkbParam}, {sridLiteral}, {MysqlAxisOrderOption})"
            : $"ST_GeomFromWKB({wkbParam}, {sridLiteral})";
    }

    public static bool IsDistanceSphereCompatibleSrid(int srid) =>
        srid is 0 or 4326;

    public const double DistanceSphereRadiusMeters = 6_371_008.8;

    public static string GeodesicDistance(string left, string right, int srid, MySqlEngineFlavor flavor)
    {
        if (flavor == MySqlEngineFlavor.Mysql && srid == 4326)
        {
            return $"ST_Distance({left}, {right})";
        }

        return DistanceSphere(left, right);
    }

    public static string DistanceSphere(string left, string right)
    {
        var radius = DistanceSphereRadiusMeters.ToString(CultureInfo.InvariantCulture);
        return $"ST_Distance_Sphere({left}, {right}, {radius})";
    }
}
