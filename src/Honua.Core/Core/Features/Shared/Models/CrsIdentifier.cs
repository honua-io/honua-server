// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Shared.Models;

// Matching is anchored, so a near-miss such as EPSG:43260 is a different CRS and is not accepted as EPSG:4326.
public static partial class CrsIdentifier
{
    public const int Wgs84EpsgCode = 4326;

    public static bool TryParseEpsgCode(string? identifier, out int epsgCode)
    {
        epsgCode = 0;

        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        var trimmed = identifier.Trim();

        if (Crs84AliasPattern().IsMatch(trimmed)
            || Crs84UrnPattern().IsMatch(trimmed)
            || Crs84UriPattern().IsMatch(trimmed))
        {
            epsgCode = Wgs84EpsgCode;
            return true;
        }

        var match = BareCodePattern().Match(trimmed);
        if (!match.Success)
        {
            match = EpsgShortFormPattern().Match(trimmed);
        }

        if (!match.Success)
        {
            match = EpsgUrnPattern().Match(trimmed);
        }

        if (!match.Success)
        {
            match = EpsgUriPattern().Match(trimmed);
        }

        if (!match.Success)
        {
            return false;
        }

        var code = match.Groups["code"].ValueSpan;
        return int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out epsgCode);
    }

    // CRS84 and EPSG:4326 differ in declared axis order and both name WGS 84 longitude/latitude; each surface fixes coordinate order by its own contract.
    public static bool IsWgs84(string? identifier)
        => TryParseEpsgCode(identifier, out var code) && code == Wgs84EpsgCode;

    // A bare positive code ("4326"). Leading zeros are refused so a single
    // identifier never has two spellings here.
    [GeneratedRegex(@"^(?<code>[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex BareCodePattern();

    // "EPSG:4326".
    [GeneratedRegex(
        @"^EPSG:(?<code>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EpsgShortFormPattern();

    // "urn:ogc:def:crs:EPSG::4326" (canonical empty version), an explicit version
    // segment ("urn:ogc:def:crs:EPSG:6.9:4326"), and the historical x-ogc
    // authority — matching the spellings SpatialReferenceHelpers accepts on the
    // WFS/FES query path (#2737).
    [GeneratedRegex(
        @"^urn:(?:x-)?ogc:def:crs:EPSG:(?:[0-9]+(?:\.[0-9]+)*)?:(?<code>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EpsgUrnPattern();

    // "http://www.opengis.net/def/crs/EPSG/0/4326" (https tolerated: Honua's own
    // Maps handler emits an https Content-Crs header).
    [GeneratedRegex(
        @"^https?://www\.opengis\.net/def/crs/EPSG/[0-9]+(?:\.[0-9]+)*/(?<code>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EpsgUriPattern();

    // Bare CRS84 aliases.
    [GeneratedRegex(
        @"^(?:CRS84|OGC:CRS84|CRS:84)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Crs84AliasPattern();

    // "urn:ogc:def:crs:OGC:1.3:CRS84", the empty-version "urn:ogc:def:crs:OGC::CRS84",
    // the omitted-version compatibility spelling "urn:ogc:def:crs:OGC:CRS84" (which
    // SpatialReferenceHelpers also accepts, so the two agree), and the WFS 2.0
    // default "urn:ogc:def:crs:OGC:2:84".
    [GeneratedRegex(
        @"^urn:(?:x-)?ogc:def:crs:OGC:(?:(?:(?:[0-9]+(?:\.[0-9]+)*)?:)?CRS84|2:84)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Crs84UrnPattern();

    // "http://www.opengis.net/def/crs/OGC/1.3/CRS84".
    [GeneratedRegex(
        @"^https?://www\.opengis\.net/def/crs/OGC/[0-9]+(?:\.[0-9]+)*/CRS84$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Crs84UriPattern();
}
