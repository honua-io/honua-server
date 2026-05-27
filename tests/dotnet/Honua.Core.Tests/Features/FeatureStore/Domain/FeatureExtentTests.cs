// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Unit coverage for <see cref="FeatureExtent"/>. The struct carries computed
/// width/height/center properties and a bidirectional bridge to the unified
/// <see cref="BoundingBox"/> type; round-tripping must not lose data (#1144).
/// </summary>
public sealed class FeatureExtentTests
{
    [UnitTest]
    public void Create_PopulatesAllRequiredFields()
    {
        var extent = FeatureExtent.Create(minX: -10, minY: -20, maxX: 10, maxY: 20, spatialReference: 4326);

        extent.MinX.Should().Be(-10);
        extent.MinY.Should().Be(-20);
        extent.MaxX.Should().Be(10);
        extent.MaxY.Should().Be(20);
        extent.SpatialReference.Should().Be(4326);
    }

    [UnitTest]
    public void Width_ReturnsXSpan()
    {
        FeatureExtent.Create(0, 0, 50, 10, 4326).Width.Should().Be(50);
    }

    [UnitTest]
    public void Height_ReturnsYSpan()
    {
        FeatureExtent.Create(0, 0, 50, 30, 4326).Height.Should().Be(30);
    }

    [UnitTest]
    public void CenterX_AveragesXBounds()
    {
        FeatureExtent.Create(-10, 0, 30, 0, 4326).CenterX.Should().Be(10);
    }

    [UnitTest]
    public void CenterY_AveragesYBounds()
    {
        FeatureExtent.Create(0, -10, 0, 30, 4326).CenterY.Should().Be(10);
    }

    [UnitTest]
    public void Width_DegenerateExtent_IsZero()
    {
        FeatureExtent.Create(5, 5, 5, 5, 4326).Width.Should().Be(0);
    }

    [UnitTest]
    public void Height_DegenerateExtent_IsZero()
    {
        FeatureExtent.Create(5, 5, 5, 5, 4326).Height.Should().Be(0);
    }

    [UnitTest]
    public void ToBoundingBox_PreservesCoordinatesAndSrid()
    {
        var extent = FeatureExtent.Create(1, 2, 3, 4, 3857);

        var bbox = extent.ToBoundingBox();

        bbox.MinX.Should().Be(1);
        bbox.MinY.Should().Be(2);
        bbox.MaxX.Should().Be(3);
        bbox.MaxY.Should().Be(4);
        bbox.SpatialReferenceId.Should().Be(3857);
    }

    [UnitTest]
    public void FromBoundingBox_PreservesCoordinatesAndSrid()
    {
        var bbox = BoundingBox.Create(1, 2, 3, 4, 3857);

        var extent = FeatureExtent.FromBoundingBox(bbox);

        extent.MinX.Should().Be(1);
        extent.MinY.Should().Be(2);
        extent.MaxX.Should().Be(3);
        extent.MaxY.Should().Be(4);
        extent.SpatialReference.Should().Be(3857);
    }

    [UnitTest]
    public void FromBoundingBox_NullSrid_DefaultsToWgs84()
    {
        var bbox = BoundingBox.Create(0, 0, 1, 1);

        FeatureExtent.FromBoundingBox(bbox).SpatialReference.Should().Be(4326);
    }

    [UnitTest]
    public void RoundTrip_BoundingBoxToFeatureExtentAndBack_IsLossless()
    {
        var original = BoundingBox.Create(-122.5, 37.7, -122.4, 37.8, 4326);

        var roundTripped = FeatureExtent.FromBoundingBox(original).ToBoundingBox();

        roundTripped.Should().Be(original);
    }

    [UnitTest]
    public void Records_EqualByValue()
    {
        var a = FeatureExtent.Create(0, 0, 1, 1, 4326);
        var b = FeatureExtent.Create(0, 0, 1, 1, 4326);

        a.Should().Be(b);
    }

    [UnitTest]
    public void Records_DifferentSrid_AreNotEqual()
    {
        var a = FeatureExtent.Create(0, 0, 1, 1, 4326);
        var b = FeatureExtent.Create(0, 0, 1, 1, 3857);

        a.Should().NotBe(b);
    }
}
