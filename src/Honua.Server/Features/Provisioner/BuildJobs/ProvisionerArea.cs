// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Validation;

namespace Honua.Server.Features.Provisioner.BuildJobs;

public enum ProvisionerAreaKind
{
    Bbox,

    StateFips,

    CountyGeoid
}

public sealed record ProvisionerArea
{
    private ProvisionerArea(ProvisionerAreaKind kind, string raw)
    {
        Kind = kind;
        Raw = raw;
    }

    public ProvisionerAreaKind Kind { get; private init; }

    public string Raw { get; private init; }

    public double[]? Bbox { get; private init; }

    public string? StateFips { get; private init; }

    public string? CountyGeoid { get; private init; }

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

        var parsed = BoundingBoxOrdinates.Parse(parts);
        if (!parsed.Succeeded)
        {
            error = "bbox values must be finite numbers";
            return false;
        }

        var nums = parsed.Ordinates;
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

    public string ToParameterValue() => Raw;
}
