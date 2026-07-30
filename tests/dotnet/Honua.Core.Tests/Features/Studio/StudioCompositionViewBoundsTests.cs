// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// The shared Studio view contract every admission surface enforces: the MCP composition tool
/// schemas advertise these bounds and the live-collaboration op-log validator rejects against them,
/// so they must live in exactly one place (honua-server#2999 review).
/// </summary>
public sealed class StudioCompositionViewBoundsTests
{
    [UnitTest]
    public void TryValidate_NullView_IsAccepted()
    {
        Assert.True(StudioCompositionViewBounds.TryValidate(null, out var error));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(24d, 85d)]
    [InlineData(12.5d, 42.5d)]
    public void TryValidate_ZoomAndPitchInsideTheInclusiveBounds_AreAccepted(double zoom, double pitch)
    {
        var view = new StudioCompositionView { Zoom = zoom, Pitch = pitch };

        Assert.True(StudioCompositionViewBounds.TryValidate(view, out var error), error);
    }

    [Theory]
    [InlineData(24.5d)]
    [InlineData(25d)]
    [InlineData(-0.5d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TryValidate_ZoomOutsideTheBounds_IsRejectedWithReason(double zoom)
    {
        Assert.False(StudioCompositionViewBounds.TryValidate(
            new StudioCompositionView { Zoom = zoom }, out var error));
        Assert.Contains("zoom", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(85.5d)]
    [InlineData(90d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void TryValidate_PitchOutsideTheBounds_IsRejectedWithReason(double pitch)
    {
        Assert.False(StudioCompositionViewBounds.TryValidate(
            new StudioCompositionView { Pitch = pitch }, out var error));
        Assert.Contains("pitch", error, StringComparison.Ordinal);
    }

    [UnitTest]
    public void TryValidate_BearingIsUnbounded()
        => Assert.True(StudioCompositionViewBounds.TryValidate(
            new StudioCompositionView { Bearing = 540 }, out _));

    [UnitTest]
    public void TryValidate_CoordinateArity_MatchesTheDeclaredOrdinateCounts()
    {
        // IReadOnlyList<double> deserializes any-length arrays, so arity is only expressible here.
        Assert.False(StudioCompositionViewBounds.TryValidate(
            new StudioCompositionView { Bbox = [0, 1, 2] }, out var bboxError));
        Assert.Contains("bbox", bboxError, StringComparison.Ordinal);

        Assert.False(StudioCompositionViewBounds.TryValidate(
            new StudioCompositionView { Center = [0, 1, 2] }, out var centerError));
        Assert.Contains("center", centerError, StringComparison.Ordinal);

        Assert.True(StudioCompositionViewBounds.TryValidate(
            new StudioCompositionView { Bbox = [0, 0, 10, 10], Center = [5, 5] }, out _));
    }
}
