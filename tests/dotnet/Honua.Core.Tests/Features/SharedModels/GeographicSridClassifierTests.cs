// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.SharedModels;

/// <summary>
/// Pins the canonical geographic-SRID classification (#2732). Two buckets are asserted separately:
/// the broad <see cref="GeographicSridClassifier.IsGeographicSrid(int)"/> used for axis order and
/// general degree/planar routing, and the deliberately-narrower geodesic-safe allowlist used by the
/// provider distance paths. The divergences the audit resolved (EPSG:4759, 4619, 4617) are pinned
/// explicitly so a future edit cannot silently re-split the lists.
/// </summary>
public sealed class GeographicSridClassifierTests
{
    [Theory]
    // Codes present in every prior list.
    [InlineData(4326)]  // WGS 84
    [InlineData(4269)]  // NAD83
    [InlineData(4267)]  // NAD27
    [InlineData(4258)]  // ETRS89
    [InlineData(4283)]  // GDA94
    // Broad axis-order codes (were in BoundingBox).
    [InlineData(4979)]  // WGS 84 3D
    [InlineData(4230)]  // ED50
    [InlineData(4277)]  // OSGB 1936
    [InlineData(7844)]  // GDA2020
    [InlineData(4490)]  // CGCS2000
    [InlineData(6668)]  // JGD2011
    [InlineData(4674)]  // SIRGAS 2000
    // Resolved divergences (#2732): previously missing from the broad axis-order list.
    [InlineData(4759)]  // NAD83(NSRS2007) — was geodesic-listed but not axis-listed.
    [InlineData(4619)]  // SWEREF99 — was only in the Postgres fallback.
    [InlineData(4617)]  // NAD83(CSRS) — Postgres fallback lacked it; now unified.
    public void IsGeographicSrid_KnownGeographicCrs_ReturnsTrue(int srid)
    {
        GeographicSridClassifier.IsGeographicSrid(srid).Should().BeTrue();
        GeographicSridClassifier.IsGeographicSrid((int?)srid).Should().BeTrue();
    }

    [Theory]
    [InlineData(3857)]   // Web Mercator (projected)
    [InlineData(2154)]   // RGF93 / Lambert-93 (projected)
    [InlineData(4978)]   // WGS 84 geocentric (metres, NOT lat/lon) — must never be geographic.
    [InlineData(32618)]  // UTM 18N (projected)
    [InlineData(4499)]   // Unassigned code inside the old 4000-4999 heuristic range.
    [InlineData(0)]
    [InlineData(-1)]
    public void IsGeographicSrid_ProjectedGeocentricOrUnknown_ReturnsFalse(int srid)
    {
        GeographicSridClassifier.IsGeographicSrid(srid).Should().BeFalse();
    }

    [UnitTest]
    public void IsGeographicSrid_NullSrid_TreatedAsGeographic()
    {
        // The default assumed CRS is WGS 84; a null SRID keeps the historical BoundingBox convention.
        GeographicSridClassifier.IsGeographicSrid((int?)null).Should().BeTrue();
    }

    [Theory]
    [InlineData(4326)]
    [InlineData(4269)]
    [InlineData(4267)]
    [InlineData(4258)]
    [InlineData(4283)]
    [InlineData(4617)]
    [InlineData(4759)]
    public void IsGeodesicDistanceSafeSrid_AllowlistedCodes_ReturnsTrue(int srid)
    {
        GeographicSridClassifier.IsGeodesicDistanceSafeSrid(srid).Should().BeTrue();
    }

    [Theory]
    // Geographic, but intentionally OUTSIDE the geodesic-safe allowlist (#2731): the WGS 84 spheroid
    // distance is not applied directly and the DuckDB provider rejects distance filters on these.
    [InlineData(4674)]  // SIRGAS 2000
    [InlineData(4230)]  // ED50
    [InlineData(4490)]  // CGCS2000
    [InlineData(4979)]  // WGS 84 3D
    // Projected / geocentric.
    [InlineData(3857)]
    [InlineData(4978)]
    public void IsGeodesicDistanceSafeSrid_UnlistedOrProjected_ReturnsFalse(int srid)
    {
        GeographicSridClassifier.IsGeodesicDistanceSafeSrid(srid).Should().BeFalse();
    }

    [UnitTest]
    public void GeodesicDistanceSafeSrids_IsSubsetOfGeographicSrids()
    {
        // The geodesic-safe allowlist must never contain a code the broad classifier rejects,
        // otherwise the distance path would run a spheroid distance on a non-geographic CRS.
        GeographicSridClassifier.GeodesicDistanceSafeSrids.Should()
            .BeSubsetOf(GeographicSridClassifier.GeographicSrids);
    }

    [UnitTest]
    public void SpatialConstants_GeographicSrids_MatchesGeodesicSafeAllowlist()
    {
        // The legacy public SpatialConstants.GeographicSrids surface is now sourced from the
        // canonical geodesic-safe list; keep them in lock-step.
        SpatialConstants.GeographicSrids.Should()
            .BeEquivalentTo(GeographicSridClassifier.GeodesicDistanceSafeSrids.ToArray());
    }
}
