// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.MySql.Features.Infrastructure;

/// <summary>
/// Engine-aware SQL fragments for MySQL/MariaDB spatial WKB I/O.
/// Centralises the axis-order option handling so callers do not duplicate the
/// per-engine branch.
/// </summary>
internal static class MySqlSpatialSql
{
    private const string MysqlAxisOrderOption = "'axis-order=long-lat'";

    /// <summary>
    /// Returns the <c>ST_AsWKB</c> fragment for the supplied (already-quoted) geometry column.
    /// On MySQL the <c>'axis-order=long-lat'</c> option forces canonical X/Y order regardless
    /// of the SRS-defined axis order; MariaDB does not support the option and uses WKB-natural
    /// order by default.
    /// </summary>
    public static string AsWkb(string quotedColumn, MySqlEngineFlavor flavor) =>
        flavor == MySqlEngineFlavor.Mysql
            ? $"ST_AsWKB({quotedColumn}, {MysqlAxisOrderOption})"
            : $"ST_AsWKB({quotedColumn})";

    /// <summary>
    /// Returns the <c>ST_GeomFromWKB</c> fragment that parses an X/Y WKB literal and tags it
    /// with <paramref name="srid"/>. On MySQL the <c>'axis-order=long-lat'</c> option matches
    /// the canonical Honua WKB contract; MariaDB uses the 2-argument form.
    /// </summary>
    public static string GeomFromWkb(string wkbParam, int srid, MySqlEngineFlavor flavor)
    {
        var sridLiteral = srid.ToString(CultureInfo.InvariantCulture);
        return flavor == MySqlEngineFlavor.Mysql
            ? $"ST_GeomFromWKB({wkbParam}, {sridLiteral}, {MysqlAxisOrderOption})"
            : $"ST_GeomFromWKB({wkbParam}, {sridLiteral})";
    }
}
