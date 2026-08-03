// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Core.Tests.Raster.ZarrParser;

public sealed class ZarrSubsetWorkEstimatorTests
{
    [Fact]
    public void Estimate_CountsIntersectingChunksAndIndependentWebBuffers()
    {
        var array = new ZarrArrayMetadata(
            "temperature",
            ZarrFormatVersion.V2,
            string.Empty,
            [100, 100],
            [16, 16],
            "<f4",
            "C",
            null,
            null,
            ["y", "x"]);
        var request = new ZarrSubsetRequest
        {
            Variable = array.Name,
            Start = [8, 8],
            Stop = [40, 40],
        };

        var work = ZarrSubsetWorkEstimator.Estimate(array, request, outputWidth: 256, outputHeight: 256);

        work.WebOutputCells.Should().Be(65_536);
        work.WebOutputBytes.Should().Be((32L * 32L * sizeof(float)) + (256L * 256L * 4L));
        work.ObjectRangeRequests.Should().Be(9);
        work.ObjectRangeBytes.Should().Be(9L * 16L * 16L * sizeof(float));
        work.PostGisWorkUnits.Should().Be(0);
    }

    [Fact]
    public void Estimate_InvalidRank_FailsBeforeAnyReadCanBeScheduled()
    {
        var array = new ZarrArrayMetadata(
            "temperature",
            ZarrFormatVersion.V2,
            string.Empty,
            [100, 100],
            [16, 16],
            "<f4",
            "C",
            null,
            null,
            ["y", "x"]);
        var request = new ZarrSubsetRequest
        {
            Variable = array.Name,
            Start = [0],
            Stop = [1],
        };

        var act = () => ZarrSubsetWorkEstimator.Estimate(array, request, 256, 256);

        act.Should().Throw<ArgumentException>();
    }
}
