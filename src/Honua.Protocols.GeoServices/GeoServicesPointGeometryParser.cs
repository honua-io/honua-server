// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Protocols.GeoServices;

/// <summary>Strict two-coordinate literal syntax shared by GeoServices identify endpoints.</summary>
internal static class GeoServicesPointGeometryParser
{
    internal static bool TryParsePointLiteral(string value, out double x, out double y)
    {
        // Esri's Identify wire examples and native QGIS use {x: number, y: number}.
        // Accept only those two finite coordinates, then use the existing point
        // validation/CRS pipeline. Do not relax the general GeoServices JSON parser.
        x = 0;
        y = 0;
        var literal = value.AsSpan().Trim();
        if (literal.Length < 2 || literal[0] != '{' || literal[^1] != '}')
        {
            return false;
        }

        var members = literal[1..^1];
        var comma = members.IndexOf(',');
        if (comma < 0 ||
            !TryParsePointLiteralMember(members[..comma], out var firstKey, out var firstValue) ||
            !TryParsePointLiteralMember(members[(comma + 1)..], out var secondKey, out var secondValue) ||
            firstKey == secondKey)
        {
            return false;
        }

        x = firstKey == 'x' ? firstValue : secondValue;
        y = firstKey == 'y' ? firstValue : secondValue;
        return true;
    }

    private static bool TryParsePointLiteralMember(ReadOnlySpan<char> member, out char key, out double value)
    {
        key = default;
        value = default;
        var colon = member.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var name = member[..colon].Trim();
        if (name.Length != 1 || name[0] is not ('x' or 'y'))
        {
            return false;
        }

        key = name[0];
        return double.TryParse(member[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }
}
