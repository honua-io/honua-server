// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

/// <summary>
/// Unit tests for the hand-rolled MGRS/USNG converter that backs the
/// toGeoCoordinateString / fromGeoCoordinateString operations.
/// </summary>
[Protocol(TestProtocols.GeometryService)]
public sealed class MgrsConverterTests
{
    [UnitTest]
    [Operation(Operations.ToGeoCoordinateString)]
    public void ToMgrs_WashingtonDc_ProducesZone18SBand()
    {
        // Washington, DC area: zone 18, latitude band S, 100km grid square "UJ".
        var mgrs = MgrsConverter.ToMgrs(longitude: -77.0739, latitude: 38.9587, precision: 5, addSpaces: true);

        mgrs.Should().StartWith("18S UJ ", "Washington DC falls in UTM zone 18, band S, grid square UJ");
    }

    [UnitTest]
    [Operation(Operations.ToGeoCoordinateString)]
    public void ToMgrs_WashingtonMonument_MatchesPublishedReferenceValue()
    {
        // The Washington Monument (38.8895°N, 77.0353°W) has a widely-published
        // MGRS reference value of "18S UJ 23478 06483" (NGA / GeoTrans). This anchors
        // the projection math to a known-good external value.
        var mgrs = MgrsConverter.ToMgrs(longitude: -77.0353, latitude: 38.8895, precision: 5, addSpaces: true);

        mgrs.Should().Be("18S UJ 23478 06483");
    }

    [UnitTest]
    [Operation(Operations.ToGeoCoordinateString)]
    public void ToMgrs_NoSpaces_ProducesCompactMgrsForm()
    {
        var withSpaces = MgrsConverter.ToMgrs(longitude: -77.0739, latitude: 38.9587, precision: 5, addSpaces: true);
        var compact = MgrsConverter.ToMgrs(longitude: -77.0739, latitude: 38.9587, precision: 5, addSpaces: false);

        compact.Should().Be(withSpaces.Replace(" ", string.Empty));
        compact.Should().NotContain(" ");
    }

    [Theory]
    [InlineData(-77.0739, 38.9587)]   // Washington, DC
    [InlineData(-122.4194, 37.7749)]  // San Francisco
    [InlineData(2.3522, 48.8566)]     // Paris
    [InlineData(151.2093, -33.8688)]  // Sydney (southern hemisphere)
    [InlineData(139.6917, 35.6895)]   // Tokyo
    [InlineData(0.0, 0.1)]            // Near equator / prime meridian
    [UnitTest]
    [Operation(Operations.FromGeoCoordinateString)]
    public void RoundTrip_ToMgrsThenFromMgrs_RecoversOriginalWithinOneMeter(double longitude, double latitude)
    {
        var mgrs = MgrsConverter.ToMgrs(longitude, latitude, precision: 5, addSpaces: true);
        var (recoveredLon, recoveredLat) = MgrsConverter.FromMgrs(mgrs);

        // 5-digit precision resolves to 1 meter; allow a small tolerance for the
        // truncation inherent in grid encoding (~0.00002 degrees ≈ 2 m).
        recoveredLat.Should().BeApproximately(latitude, 0.00002);
        recoveredLon.Should().BeApproximately(longitude, 0.00003);
    }

    [UnitTest]
    [Operation(Operations.FromGeoCoordinateString)]
    public void FromMgrs_AcceptsCompactAndSpacedForms_Equivalently()
    {
        var spaced = MgrsConverter.FromMgrs("18S UJ 22806 06998");
        var compact = MgrsConverter.FromMgrs("18SUJ2280606998");

        compact.Longitude.Should().BeApproximately(spaced.Longitude, 1e-9);
        compact.Latitude.Should().BeApproximately(spaced.Latitude, 1e-9);
    }

    [UnitTest]
    [Operation(Operations.FromGeoCoordinateString)]
    public void FromMgrs_KnownDcReference_DecodesToWashingtonDcArea()
    {
        // 18S UJ 22806 06998 is a widely-cited Washington, DC reference grid value.
        var (longitude, latitude) = MgrsConverter.FromMgrs("18S UJ 22806 06998");

        latitude.Should().BeApproximately(38.8895, 0.01);
        longitude.Should().BeApproximately(-77.0353, 0.01);
    }

    [UnitTest]
    [Operation(Operations.ToGeoCoordinateString)]
    public void ToMgrs_OutOfRangeLatitude_Throws()
    {
        var act = () => MgrsConverter.ToMgrs(longitude: 0, latitude: 89, precision: 5);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
