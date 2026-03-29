// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.SharedModels;

/// <summary>
/// Tests for BoundingBox antimeridian-aware operations and edge cases.
/// </summary>
public class BoundingBoxAntimeridianTests
{
    [UnitTest]
    public void IsAntimeridianCrossing_WhenMinXGreaterThanMaxX_ReturnsTrue()
    {
        var bbox = BoundingBox.Create(170, -10, -170, 10, 4326);

        bbox.IsAntimeridianCrossing.Should().BeTrue();
    }

    [UnitTest]
    public void IsAntimeridianCrossing_WhenNormalBbox_ReturnsFalse()
    {
        var bbox = BoundingBox.Create(-10, -10, 10, 10, 4326);

        bbox.IsAntimeridianCrossing.Should().BeFalse();
    }

    [UnitTest]
    public void IsAntimeridianCrossing_AtExactBoundary_ReturnsFalse()
    {
        // MinX == MaxX at 180 is not a crossing, it's a degenerate line
        var bbox = BoundingBox.Create(180, -10, 180, 10, 4326);

        bbox.IsAntimeridianCrossing.Should().BeFalse();
    }

    [UnitTest]
    public void Width_WithCrossing_SumsBothRanges()
    {
        // 170 to 180 (east) + -180 to -170 (west) = 20 degrees
        var bbox = BoundingBox.Create(170, -10, -170, 10, 4326);

        bbox.Width.Should().BeApproximately(20.0, 1e-9);
    }

    [UnitTest]
    public void Width_WithLargeCrossing_CalculatesCorrectly()
    {
        // 10 to 180 (east) + -180 to -10 (west) = 340 degrees
        var bbox = BoundingBox.Create(10, -10, -10, 10, 4326);

        bbox.Width.Should().BeApproximately(340.0, 1e-9);
    }

    [UnitTest]
    public void Intersects_CrossingWithEasternSubset_ReturnsTrue()
    {
        var crossing = BoundingBox.Create(170, -10, -170, 10, 4326);
        var eastern = BoundingBox.Create(175, -5, 179, 5, 4326);

        crossing.Intersects(eastern).Should().BeTrue();
    }

    [UnitTest]
    public void Intersects_CrossingWithWesternSubset_ReturnsTrue()
    {
        var crossing = BoundingBox.Create(170, -10, -170, 10, 4326);
        var western = BoundingBox.Create(-179, -5, -175, 5, 4326);

        crossing.Intersects(western).Should().BeTrue();
    }

    [UnitTest]
    public void Intersects_CrossingWithDisjointMiddle_ReturnsFalse()
    {
        var crossing = BoundingBox.Create(170, -10, -170, 10, 4326);
        var middle = BoundingBox.Create(-50, -5, -40, 5, 4326);

        crossing.Intersects(middle).Should().BeFalse();
    }

    [UnitTest]
    public void Intersects_TwoCrossings_OverlappingOnEasternSide_ReturnsTrue()
    {
        var crossing1 = BoundingBox.Create(170, -10, -170, 10, 4326);
        var crossing2 = BoundingBox.Create(175, -5, -175, 5, 4326);

        crossing1.Intersects(crossing2).Should().BeTrue();
    }

    [UnitTest]
    public void Union_AcrossAntimeridian_PreservesCrossing()
    {
        var east = BoundingBox.Create(170, -10, 179, 10, 4326);
        var west = BoundingBox.Create(-179, -10, -170, 10, 4326);

        var union = east.Union(west);

        union.IsAntimeridianCrossing.Should().BeTrue();
        union.MinX.Should().Be(170);
        union.MaxX.Should().Be(-170);
    }

    [UnitTest]
    public void Intersection_BothCrossing_PreservesCrossing()
    {
        var wider = BoundingBox.Create(170, -10, -170, 10, 4326);
        var narrower = BoundingBox.Create(175, -5, -175, 5, 4326);

        var intersection = wider.Intersection(narrower);

        intersection.Should().NotBeNull();
        intersection!.Value.IsAntimeridianCrossing.Should().BeTrue();
        intersection.Value.MinX.Should().Be(175);
        intersection.Value.MaxX.Should().Be(-175);
    }

    [UnitTest]
    public void Contains_CrossingBoxContainsEasternPoint_ReturnsTrue()
    {
        var crossing = BoundingBox.Create(170, -10, -170, 10, 4326);
        var point = BoundingBox.Create(175, 0, 175, 0, 4326);

        crossing.Contains(point).Should().BeTrue();
    }

    [UnitTest]
    public void Contains_CrossingBoxContainsWesternPoint_ReturnsTrue()
    {
        var crossing = BoundingBox.Create(170, -10, -170, 10, 4326);
        var point = BoundingBox.Create(-175, 0, -175, 0, 4326);

        crossing.Contains(point).Should().BeTrue();
    }

    [UnitTest]
    public void Contains_CrossingBoxDoesNotContainMiddlePoint_ReturnsFalse()
    {
        var crossing = BoundingBox.Create(170, -10, -170, 10, 4326);
        var point = BoundingBox.Create(0, 0, 0, 0, 4326);

        crossing.Contains(point).Should().BeFalse();
    }

    [UnitTest]
    public void ResolveMinimalLongitudeSpan_BoundaryValues_ProducesValidResult()
    {
        // Full globe extent should not be antimeridian crossing
        var fullGlobe = BoundingBox.Create(-180, -90, 180, 90, 4326);

        fullGlobe.IsAntimeridianCrossing.Should().BeFalse();
        fullGlobe.Width.Should().BeApproximately(360.0, 1e-9);
    }

    [UnitTest]
    public void BoundingBox_AtPlusMinus180_IsValid()
    {
        var atPositive180 = BoundingBox.Create(179, -10, 180, 10, 4326);
        var atNegative180 = BoundingBox.Create(-180, -10, -179, 10, 4326);

        atPositive180.IsValid.Should().BeTrue();
        atNegative180.IsValid.Should().BeTrue();
    }
}
