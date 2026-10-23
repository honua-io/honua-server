// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// SQL for the linear unit of a projected CRS, read from PostGIS <c>spatial_ref_sys</c>.
/// </summary>
internal static class PostgresCrsUnitSql
{
    /// <summary>
    /// Scalar SQL for metres per projected unit of <paramref name="srid"/>. Preference matches
    /// <see cref="CrsLinearUnitFactor"/>: <c>+to_meter</c>, then <c>+units</c>, otherwise 1 (also
    /// when the CRS has no <c>spatial_ref_sys</c> row).
    /// </summary>
    public static string MetersPerProjectedUnit(int srid)
    {
        var usFoot = CrsLinearUnitFactor.UsSurveyFootMeters.ToString("R", CultureInfo.InvariantCulture);
        var foot = CrsLinearUnitFactor.InternationalFootMeters.ToString("R", CultureInfo.InvariantCulture);
        return "COALESCE((SELECT CASE "
            + "WHEN COALESCE(proj4text, '') ~ '\\+to_meter=' THEN NULLIF(substring(proj4text FROM '\\+to_meter=([0-9.eE+-]+)'), '')::double precision "
            + $"WHEN COALESCE(proj4text, '') ILIKE '%+units=us-ft%' OR COALESCE(proj4text, '') ILIKE '%+units=ftus%' THEN {usFoot} "
            + $"WHEN COALESCE(proj4text, '') ILIKE '%+units=ft%' OR COALESCE(proj4text, '') ILIKE '%+units=foot%' THEN {foot} "
            + "WHEN COALESCE(proj4text, '') ILIKE '%+units=ind-ft%' THEN 0.3047995102481469 "
            + "WHEN COALESCE(proj4text, '') ILIKE '%+units=km%' THEN 1000 "
            + "WHEN COALESCE(proj4text, '') ILIKE '%+units=m%' OR COALESCE(proj4text, '') ILIKE '%+units=meter%' OR COALESCE(proj4text, '') ILIKE '%+units=metre%' THEN 1 "
            + "ELSE NULL END FROM spatial_ref_sys WHERE srid = "
            + srid.ToString(CultureInfo.InvariantCulture)
            + " LIMIT 1), 1.0)";
    }
}
