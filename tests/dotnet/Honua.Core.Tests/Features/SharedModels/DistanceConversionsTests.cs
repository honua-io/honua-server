// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.SharedModels;

/// <summary>
/// Unit coverage for <see cref="DistanceConversions"/>. The numeric factors
/// and the SRID classification matter for distance-based queries — drift
/// changes answers returned to clients (#1144).
/// </summary>
public sealed class DistanceConversionsTests
{
    private const double Tolerance = 1e-9;

    [UnitTest]
    public void ToMeters_Meters_IsIdentity()
    {
        DistanceConversions.ToMeters(5.0, DistanceUnit.Meters).Should().Be(5.0);
    }

    [UnitTest]
    public void ToMeters_Feet_AppliesFeetFactor()
    {
        DistanceConversions.ToMeters(10.0, DistanceUnit.Feet)
            .Should().BeApproximately(10.0 * SpatialConstants.FeetToMeters, Tolerance);
    }

    [UnitTest]
    public void ToMeters_Kilometers_Scales1000()
    {
        DistanceConversions.ToMeters(2.5, DistanceUnit.Kilometers)
            .Should().BeApproximately(2500.0, Tolerance);
    }

    [UnitTest]
    public void ToMeters_Miles_AppliesMilesFactor()
    {
        DistanceConversions.ToMeters(1.0, DistanceUnit.Miles)
            .Should().BeApproximately(SpatialConstants.MilesToMeters, Tolerance);
    }

    [Theory]
    [InlineData("meters")]
    [InlineData("m")]
    [InlineData("meter")]
    [InlineData("METERS")]
    [InlineData("Meters")]
    public void ToMeters_StringMeters_IsIdentity(string unit)
    {
        DistanceConversions.ToMeters(7.0, unit).Should().Be(7.0);
    }

    [Theory]
    [InlineData("feet")]
    [InlineData("ft")]
    [InlineData("foot")]
    [InlineData("FEET")]
    public void ToMeters_StringFeet_AppliesFeetFactor(string unit)
    {
        DistanceConversions.ToMeters(10.0, unit)
            .Should().BeApproximately(10.0 * SpatialConstants.FeetToMeters, Tolerance);
    }

    [Theory]
    [InlineData("kilometers")]
    [InlineData("km")]
    [InlineData("kilometer")]
    public void ToMeters_StringKilometers_Scales1000(string unit)
    {
        DistanceConversions.ToMeters(3.0, unit)
            .Should().BeApproximately(3000.0, Tolerance);
    }

    [Theory]
    [InlineData("miles")]
    [InlineData("mi")]
    [InlineData("mile")]
    public void ToMeters_StringMiles_AppliesMilesFactor(string unit)
    {
        DistanceConversions.ToMeters(1.0, unit)
            .Should().BeApproximately(SpatialConstants.MilesToMeters, Tolerance);
    }

    [UnitTest]
    public void ToMeters_UnknownUnitString_ReturnsInputUnchanged()
    {
        // Defensive: unknown unit names fall through as identity so callers
        // never silently produce wildly wrong distances on a typo.
        DistanceConversions.ToMeters(5.0, "lightyears").Should().Be(5.0);
    }

    [UnitTest]
    public void ToMeters_ZeroDistance_StaysZero()
    {
        DistanceConversions.ToMeters(0.0, DistanceUnit.Miles).Should().Be(0.0);
    }

    [UnitTest]
    public void ToMeters_NegativeDistance_PreservesSign()
    {
        DistanceConversions.ToMeters(-5.0, DistanceUnit.Kilometers)
            .Should().BeApproximately(-5000.0, Tolerance);
    }

    [Theory]
    [InlineData(4326)]
    [InlineData(4269)]
    [InlineData(4267)]
    [InlineData(4258)]
    [InlineData(4283)]
    [InlineData(4617)]
    [InlineData(4759)]
    public void IsGeographicSrid_KnownGeographicSrids_ReturnsTrue(int srid)
    {
        DistanceConversions.IsGeographicSrid(srid).Should().BeTrue();
    }

    [Theory]
    [InlineData(3857)]  // Web Mercator
    [InlineData(2154)]  // RGF93 / Lambert-93
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void IsGeographicSrid_ProjectedOrUnknown_ReturnsFalse(int srid)
    {
        DistanceConversions.IsGeographicSrid(srid).Should().BeFalse();
    }
}
