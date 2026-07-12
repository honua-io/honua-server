// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.SharedModels;

/// <summary>
/// Unit coverage for <see cref="WebMercatorMath"/>, focused on the antimeridian-preserving
/// extent transform (#2739).
/// </summary>
public sealed class WebMercatorMathTests
{
    [UnitTest]
    public void TransformSampledExtent_NonWrapped_ReturnsOrderedBounds()
    {
        var (minX, minY, maxX, maxY) = WebMercatorMath.TransformSampledExtent(
            -10.0,
            -10.0,
            10.0,
            10.0,
            WebMercatorMath.LonLatToWebMercator,
            4);

        var expectedWest = WebMercatorMath.LonLatToWebMercator(-10.0, 0.0).X;
        var expectedEast = WebMercatorMath.LonLatToWebMercator(10.0, 0.0).X;

        minX.Should().BeApproximately(expectedWest, 1.0);
        maxX.Should().BeApproximately(expectedEast, 1.0);
        minX.Should().BeLessThan(maxX);
        minY.Should().BeLessThan(maxY);
    }

    [UnitTest]
    public void TransformSampledExtent_AntimeridianCrossing_PreservesWrappedExtent()
    {
        // Extent 170,-10 -> -170,10 crosses the dateline (minX > maxX). The transform must keep
        // the crossing: the western edge (170) becomes MinX and the eastern edge (-170) becomes
        // MaxX, so MinX > MaxX — instead of collapsing to a single inflated [-max,+max] span.
        var (minX, minY, maxX, maxY) = WebMercatorMath.TransformSampledExtent(
            170.0,
            -10.0,
            -170.0,
            10.0,
            WebMercatorMath.LonLatToWebMercator,
            4);

        var west = WebMercatorMath.LonLatToWebMercator(170.0, 0.0).X;
        var east = WebMercatorMath.LonLatToWebMercator(-170.0, 0.0).X;
        var south = WebMercatorMath.LonLatToWebMercator(0.0, -10.0).Y;
        var north = WebMercatorMath.LonLatToWebMercator(0.0, 10.0).Y;

        minX.Should().BeApproximately(west, 1.0);
        maxX.Should().BeApproximately(east, 1.0);
        minX.Should().BeGreaterThan(maxX);
        minY.Should().BeApproximately(south, 1.0);
        maxY.Should().BeApproximately(north, 1.0);
    }
}
