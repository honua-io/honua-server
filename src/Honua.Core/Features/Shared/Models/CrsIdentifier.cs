// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Strict, validation-grade recognition of the CRS identifier spellings Honua
/// accepts on the wire: the <c>EPSG:&lt;code&gt;</c> short form, the OGC URN
/// (<c>urn:ogc:def:crs:EPSG::&lt;code&gt;</c>), the OGC HTTP URI
/// (<c>http://www.opengis.net/def/crs/EPSG/0/&lt;code&gt;</c>), and the CRS84
/// aliases for WGS 84 longitude/latitude.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately an <em>exact</em> matcher: every pattern is anchored, so
/// <c>EPSG:43260</c> resolves to EPSG 43260 (a different CRS) and
/// <c>NOT_CRS84</c> resolves to nothing at all. Use it wherever a declared CRS
/// gates acceptance of a payload, in place of substring probes for
/// <c>"4326"</c>/<c>"CRS84"</c>, which admit contrived near-misses
/// (honua-server#3053).
/// </para>
/// <para>
/// It is distinct from <see cref="ExtentExtensions.TryExtractSridFromCrs"/>,
/// which is a deliberately lenient best-effort <em>extractor</em> used to read
/// an SRID out of identifiers emitted by other servers. Extraction wants
/// leniency; validation wants exactness — so the two are kept apart rather than
/// one being bent into the other.
/// </para>
/// </remarks>
public static partial class CrsIdentifier
{
    /// <summary>EPSG code for WGS 84 (and its CRS84 longitude/latitude alias).</summary>
    public const int Wgs84EpsgCode = 4326;

    /// <summary>
    /// Attempts to resolve a CRS identifier to its EPSG code.
    /// </summary>
    /// <param name="identifier">
    /// The identifier to parse. Surrounding whitespace is ignored; matching is
    /// case-insensitive, as authority and scheme tokens are by specification.
    /// </param>
    /// <param name="epsgCode">The resolved EPSG code when the identifier is recognized.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="identifier"/> is one of the
    /// recognized spellings; otherwise <see langword="false"/>. Unrecognized
    /// identifiers — including well-known-text, Esri WKID JSON, and free text —
    /// return <see langword="false"/> rather than a guess.
    /// </returns>
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

    /// <summary>
    /// Determines whether a CRS identifier names WGS 84 longitude/latitude
    /// (EPSG:4326 or its CRS84 aliases).
    /// </summary>
    /// <param name="identifier">The identifier to test.</param>
    /// <returns>
    /// <see langword="true"/> only when the identifier is a recognized spelling
    /// resolving to EPSG <see cref="Wgs84EpsgCode"/>. CRS84 and EPSG:4326 differ
    /// in declared axis order but name the same coordinate reference system, and
    /// every Honua surface that consults this fixes coordinate order by its own
    /// contract (RFC 7946 GeoJSON is longitude/latitude), so both answer true.
    /// </returns>
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

    // "urn:ogc:def:crs:OGC:1.3:CRS84", "urn:ogc:def:crs:OGC::CRS84", and the
    // WFS 2.0 default "urn:ogc:def:crs:OGC:2:84".
    [GeneratedRegex(
        @"^urn:(?:x-)?ogc:def:crs:OGC:(?:(?:[0-9]+(?:\.[0-9]+)*)?:CRS84|2:84)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Crs84UrnPattern();

    // "http://www.opengis.net/def/crs/OGC/1.3/CRS84".
    [GeneratedRegex(
        @"^https?://www\.opengis\.net/def/crs/OGC/[0-9]+(?:\.[0-9]+)*/CRS84$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Crs84UriPattern();
}
