// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Provisioner.BuildJobs;

/// <summary>
/// How a per-area build job selects the subset of a feedstock layer it operates on.
/// Mirrors the AREA forms understood by the open-data provisioner
/// (<c>scripts/provisioner/provision_area.py</c>): a lon/lat bounding box or a US
/// Census GEOID (state FIPS or 5-digit county GEOID). Keeping this taxonomy aligned
/// with the catalog's <c>areaParam.accepts</c> means the same area string drives the
/// feature import, the PMTiles job, and the geocoder/router build jobs.
/// </summary>
public enum ProvisionerAreaKind
{
    /// <summary>Lon/lat envelope: <c>minLon,minLat,maxLon,maxLat</c>.</summary>
    Bbox,

    /// <summary>2-digit Census state FIPS code.</summary>
    StateFips,

    /// <summary>5-digit Census county GEOID (state FIPS + county code).</summary>
    CountyGeoid
}

/// <summary>
/// A validated, normalized area selector for a per-area geocoder/router build job.
/// Parsed from the same <c>bbox:</c> / <c>geoid:</c> string forms the Python
/// provisioner accepts, so a Maui build job can be driven by, e.g.,
/// <c>geoid:15009</c> (Maui County) or <c>bbox:-156.70,20.57,-155.98,21.03</c>.
/// </summary>
public sealed record ProvisionerArea
{
    private ProvisionerArea(ProvisionerAreaKind kind, string raw)
    {
        Kind = kind;
        Raw = raw;
    }

    /// <summary>The selector kind.</summary>
    public ProvisionerAreaKind Kind { get; private init; }

    /// <summary>The original, verbatim area string the selector was parsed from.</summary>
    public string Raw { get; private init; }

    /// <summary>Bounding box <c>[minLon, minLat, maxLon, maxLat]</c> when <see cref="Kind"/> is <see cref="ProvisionerAreaKind.Bbox"/>.</summary>
    public double[]? Bbox { get; private init; }

    /// <summary>2-digit Census state FIPS, set for both state and county selectors.</summary>
    public string? StateFips { get; private init; }

    /// <summary>5-digit Census county GEOID when <see cref="Kind"/> is <see cref="ProvisionerAreaKind.CountyGeoid"/>.</summary>
    public string? CountyGeoid { get; private init; }

    /// <summary>
    /// Parses an area string. Returns <c>false</c> with a caller-facing
    /// <paramref name="error"/> instead of throwing so the submission path can surface
    /// a clean 400 rather than a 500.
    /// </summary>
    /// <param name="value">A <c>bbox:minLon,minLat,maxLon,maxLat</c> or <c>geoid:DD[DDD]</c> string.</param>
    /// <param name="area">The parsed area on success.</param>
    /// <param name="error">A human-readable reason on failure.</param>
    public static bool TryParse(string? value, out ProvisionerArea area, out string error)
    {
        area = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "area is required (expected 'bbox:minLon,minLat,maxLon,maxLat' or 'geoid:DD[DDD]')";
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.StartsWith("bbox:", StringComparison.Ordinal))
        {
            return TryParseBbox(trimmed, out area, out error);
        }

        if (trimmed.StartsWith("geoid:", StringComparison.Ordinal))
        {
            return TryParseGeoid(trimmed, out area, out error);
        }

        error = "area must start with 'bbox:' or 'geoid:'";
        return false;
    }

    private static bool TryParseBbox(string value, out ProvisionerArea area, out string error)
    {
        area = null!;
        error = string.Empty;

        var body = value["bbox:".Length..];
        var parts = body.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            error = "bbox must have four comma-separated values: minLon,minLat,maxLon,maxLat";
            return false;
        }

        var nums = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out nums[i])
                || double.IsNaN(nums[i]) || double.IsInfinity(nums[i]))
            {
                error = "bbox values must be finite numbers";
                return false;
            }
        }

        var (minLon, minLat, maxLon, maxLat) = (nums[0], nums[1], nums[2], nums[3]);
        if (minLon < -180 || maxLon > 180 || minLat < -90 || maxLat > 90)
        {
            error = "bbox is out of range (lon in [-180,180], lat in [-90,90])";
            return false;
        }

        if (minLon >= maxLon || minLat >= maxLat)
        {
            error = "bbox must have minLon < maxLon and minLat < maxLat";
            return false;
        }

        area = new ProvisionerArea(ProvisionerAreaKind.Bbox, value) { Bbox = nums };
        return true;
    }

    private static bool TryParseGeoid(string value, out ProvisionerArea area, out string error)
    {
        area = null!;
        error = string.Empty;

        var code = value["geoid:".Length..].Trim();
        if (code.Length == 0 || !code.All(char.IsAsciiDigit))
        {
            error = "geoid must be numeric (2-digit state FIPS or 5-digit county GEOID)";
            return false;
        }

        switch (code.Length)
        {
            case 2:
                area = new ProvisionerArea(ProvisionerAreaKind.StateFips, value) { StateFips = code };
                return true;
            case 5:
                area = new ProvisionerArea(ProvisionerAreaKind.CountyGeoid, value)
                {
                    CountyGeoid = code,
                    StateFips = code[..2]
                };
                return true;
            default:
                error = "geoid must be 2 digits (state) or 5 digits (county)";
                return false;
        }
    }

    /// <summary>
    /// Encodes the selector as a single, round-trippable parameter value carried on the
    /// execution-job spec (e.g. <c>bbox:-156.70,20.57,-155.98,21.03</c>). This is exactly
    /// <see cref="Raw"/>; kept as a method so callers express intent.
    /// </summary>
    public string ToParameterValue() => Raw;
}
