// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Services;

/// <summary>
/// Shared utilities for parsing spatial reference system identifiers.
/// Handles EPSG codes, OGC URIs, and bare SRID numbers across all protocol features.
/// </summary>
internal static class SpatialReferenceHelpers
{
    public const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string EpsgUriPrefix = "http://www.opengis.net/def/crs/EPSG/0/";
    private static readonly Regex _epsgCodePattern = new(
        @"^EPSG:(?<code>[1-9]\d*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _epsgUrnPattern = new(
        @"^urn:ogc:def:crs:EPSG::(?<code>[1-9]\d*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _epsgUriPattern = new(
        @"^https?://www\.opengis\.net/def/crs/EPSG/0/(?<code>[1-9]\d*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _crs84UriPattern = new(
        @"^https?://www\.opengis\.net/def/crs/OGC/1\.3/CRS84$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _crs84UrnPattern = new(
        @"^urn:ogc:def:crs:OGC:(?:1\.3:|:)?CRS84$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a CRS string into an SRID integer.
    /// Supports EPSG:XXXX, OGC URI format, safe CURIE format, CRS84, and bare SRID numbers.
    /// </summary>
    /// <param name="crs">The CRS string to parse (e.g., "EPSG:4326", "http://www.opengis.net/def/crs/EPSG/0/4326", "4326")</param>
    /// <returns>The SRID if successfully parsed, null otherwise</returns>
    public static int? TryParseSrid(string? crs)
        => TryParseCrsDefinition(crs, out var definition)
            ? definition.Srid
            : null;

    /// <summary>
    /// Parses a CRS string into a canonical definition with axis-order metadata.
    /// </summary>
    public static bool TryParseCrsDefinition(string? crs, out CrsDefinition definition)
    {
        definition = default;

        if (string.IsNullOrWhiteSpace(crs))
        {
            return false;
        }

        var normalized = TrimWrappers(crs);

        if (IsCrs84Identifier(normalized))
        {
            definition = new CrsDefinition(Crs84Uri, SpatialReference.WGS84.Wkid, AxisOrder.EastNorth, true);
            return true;
        }

        if (TryParsePositiveInt(normalized, out var srid) ||
            TryParseRegexSrid(normalized, _epsgCodePattern, out srid) ||
            TryParseRegexSrid(normalized, _epsgUrnPattern, out srid) ||
            TryParseRegexSrid(normalized, _epsgUriPattern, out srid))
        {
            definition = CreateEpsgDefinition(srid);
            return true;
        }

        return false;
    }

    private static string TrimWrappers(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length > 1 &&
            ((normalized[0] == '[' && normalized[^1] == ']') ||
             (normalized[0] == '<' && normalized[^1] == '>')))
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }

    private static bool IsCrs84Identifier(string value)
    {
        return value.Equals("CRS84", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("CRS:84", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("OGC:CRS84", StringComparison.OrdinalIgnoreCase) ||
               _crs84UriPattern.IsMatch(value) ||
               _crs84UrnPattern.IsMatch(value);
    }

    private static CrsDefinition CreateEpsgDefinition(int srid)
    {
        var isGeographic = SpatialReference.Create(srid).IsGeographic;
        var axisOrder = isGeographic ? AxisOrder.NorthEast : AxisOrder.EastNorth;
        return new CrsDefinition(
            FormattableString.Invariant($"{EpsgUriPrefix}{srid}"),
            srid,
            axisOrder,
            isGeographic);
    }

    private static bool TryParseRegexSrid(string value, Regex regex, out int parsed)
    {
        parsed = 0;
        var match = regex.Match(value);
        return match.Success &&
               TryParsePositiveInt(match.Groups["code"].Value, out parsed);
    }

    private static bool TryParsePositiveInt(string value, out int parsed)
    {
        parsed = 0;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
    }
}
