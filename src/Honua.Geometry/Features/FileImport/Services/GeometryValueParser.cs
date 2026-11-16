// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.FileImport.Services;

internal static class GeometryValueParser
{
    internal static NtsGeometry? TryParse(string? value, WKTReader wktReader, WKBReader wkbReader)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        // EWKT: SRID=<n>;<wkt>. The CRS-detector strips this exact prefix; mirror that here
        // and honour the declared SRID on the parsed geometry.
        if (TryStripSridPrefix(trimmed, out var srid, out var wktBody))
        {
            var geometry = TryReadWkt(wktBody, wktReader);
            if (geometry != null && srid.HasValue)
            {
                geometry.SRID = srid.Value;
            }

            return geometry;
        }

        // WKB / EWKB hex. A hex payload contains only hex digits (no whitespace, parentheses,
        // commas, or WKT keywords), so this never collides with WKT text. WKBReader.HandleSRID
        // reads the EWKB SRID flag when present.
        if (LooksLikeHex(trimmed))
        {
            var geometry = TryReadWkbHex(trimmed, wkbReader);
            if (geometry != null)
            {
                return geometry;
            }
        }

        // Plain WKT.
        return TryReadWkt(trimmed, wktReader);
    }

    internal static WKBReader CreateWkbReader() => new() { HandleSRID = true };

    private static bool TryStripSridPrefix(string value, out int? srid, out string wktBody)
    {
        srid = null;
        wktBody = value;

        if (!value.StartsWith("SRID=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var semicolon = value.IndexOf(';');
        if (semicolon <= 5)
        {
            return false;
        }

        var sridText = value.Substring(5, semicolon - 5).Trim();
        if (int.TryParse(sridText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSrid) &&
            parsedSrid > 0)
        {
            srid = parsedSrid;
        }

        wktBody = value[(semicolon + 1)..].Trim();
        return true;
    }

    private static bool LooksLikeHex(string value)
    {
        // A WKB point serialises to 21 bytes (42 hex chars); require an even length and a
        // sensible minimum so short attribute tokens are not misread as geometry.
        if (value.Length < 10 || (value.Length & 1) != 0)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var isHex = ch is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static NtsGeometry? TryReadWkbHex(string hex, WKBReader wkbReader)
    {
        try
        {
            var bytes = Convert.FromHexString(hex);
            return wkbReader.Read(bytes);
        }
        catch (Exception ex) when (ex is FormatException or NetTopologySuite.IO.ParseException or ArgumentException)
        {
            return null;
        }
    }

    private static NtsGeometry? TryReadWkt(string wkt, WKTReader wktReader)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return null;
        }

        try
        {
            return wktReader.Read(wkt);
        }
        catch (Exception ex) when (ex is NetTopologySuite.IO.ParseException or ArgumentException or FormatException)
        {
            return null;
        }
    }
}
