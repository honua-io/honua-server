// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Shared parser for text geometry values that appear in CSV geometry columns and
/// <c>.wkt</c> files. Understands three encodings that geospatial exports (notably
/// standard PostGIS <c>COPY</c> / <c>ST_AsEWKB</c> output) emit, which the bare
/// <see cref="WKTReader"/> cannot read on its own:
/// <list type="bullet">
///   <item>plain OGC WKT — <c>POINT(1 2)</c>;</item>
///   <item>EWKT with a leading <c>SRID=&lt;n&gt;;</c> prefix — <c>SRID=4326;POINT(1 2)</c>
///     (the SRID is honoured on the returned geometry);</item>
///   <item>WKB / EWKB hex — <c>0101000020E6100000...</c>.</item>
/// </list>
/// Centralising this keeps the CSV and WKT readers from diverging on which encodings
/// they accept.
/// </summary>
internal static class GeometryValueParser
{
    /// <summary>
    /// Attempts to parse <paramref name="value"/> as plain WKT, EWKT (<c>SRID=n;</c> prefix),
    /// or WKB/EWKB hex. Returns <see langword="null"/> when the value is blank or cannot be
    /// parsed by any of the supported encodings. The supplied readers are reused across calls
    /// because <see cref="WKTReader"/>/<see cref="WKBReader"/> are not cheap to allocate and
    /// are not thread-safe; callers must not share them across threads.
    /// </summary>
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

    /// <summary>
    /// Creates a <see cref="WKBReader"/> configured to honour the SRID/Z/M flags that PostGIS
    /// EWKB carries. Callers should cache one per reader instance.
    /// </summary>
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
