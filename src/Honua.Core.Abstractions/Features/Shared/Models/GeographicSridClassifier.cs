// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;

#pragma warning disable CA1716 // Preserve the stable Shared namespace used across existing contracts.
namespace Honua.Core.Features.Shared.Models;

public static class GeographicSridClassifier
{
    public static readonly ImmutableArray<int> GeographicSrids =
    [
        4149,   // CH1903
        4167,   // NZGD2000
        4171,   // RGF93
        4230,   // ED50
        4258,   // ETRS89
        4267,   // NAD27
        4269,   // NAD83
        4275,   // NTF
        4277,   // OSGB 1936
        4283,   // GDA94
        4326,   // WGS 84
        4490,   // CGCS2000
        4612,   // JGD2000
        4617,   // NAD83(CSRS)
        4618,   // SAD69
        4619,   // SWEREF99
        4674,   // SIRGAS 2000
        4759,   // NAD83(NSRS2007)
        4979,   // WGS 84 (3D)
        6668,   // JGD2011
        7844,   // GDA2020
    ];

    // Do not add EPSG:4267 here; a direct spheroid distance would skip the NAD27 datum shift.
    public static readonly ImmutableArray<int> GeodesicDistanceSafeSrids =
    [
        4258,   // ETRS89
        4269,   // NAD83
        4283,   // GDA94
        4326,   // WGS 84
        4617,   // NAD83(CSRS)
        4759,   // NAD83(NSRS2007)
    ];

    private static readonly FrozenSet<int> GeographicSridSet = GeographicSrids.ToFrozenSet();

    private static readonly FrozenSet<int> GeodesicDistanceSafeSridSet = GeodesicDistanceSafeSrids.ToFrozenSet();

    // These geocentric codes are metres from the geocentre and must not be treated as latitude and longitude by the range heuristic.
    private static readonly FrozenSet<int> GeocentricSridsInGeographicBlock = new[] { 4936, 4978, 4984 }.ToFrozenSet();

    public static bool IsGeographicSrid(int? srid)
        => srid is null || GeographicSridSet.Contains(srid.Value);

    public static bool IsGeographicSrid(int srid)
        => GeographicSridSet.Contains(srid);

    // The 4000–4999 range is an offline mensuration fallback, not an authoritative geographic classification, and must not decide axis order or geodesic-distance routing.
    public static bool IsGeographicOrUnlistedGeographicRangeSrid(int srid)
        => IsGeographicSrid(srid)
           || (srid is >= 4000 and <= 4999 && !GeocentricSridsInGeographicBlock.Contains(srid));

    public static bool IsGeodesicDistanceSafeSrid(int srid)
        => GeodesicDistanceSafeSridSet.Contains(srid);
}
