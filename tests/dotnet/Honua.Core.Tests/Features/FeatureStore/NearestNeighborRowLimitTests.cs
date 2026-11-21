// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class NearestNeighborRowLimitTests
{
    private static readonly byte[] Origin = [1, 2, 3];

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(3, 5, 3)]
    [InlineData(5, 5, 5)]
    [InlineData(1_000_000, 100, 100)]
    [InlineData(7, null, 7)]
    public void NearestCountNeverWidensThePage(int nearestCount, int? limit, int expectedRows)
        => Nearest(nearestCount, limit).ResolveNearestNeighborRowLimit().Should().Be(expectedRows);

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(10, 5)]
    [InlineData(1_000_000, 100)]
    public void CountAboveThePageExceedsTheLimit(int nearestCount, int limit)
        => Nearest(nearestCount, limit).NearestNeighborCountExceedsLimit().Should().BeTrue();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(3, 5)]
    [InlineData(5, 5)]
    [InlineData(7, null)]
    public void CountWithinThePageDoesNotExceedTheLimit(int nearestCount, int? limit)
        => Nearest(nearestCount, limit).NearestNeighborCountExceedsLimit().Should().BeFalse();

    [Trait("Tier", "Fast")]
    [Fact]
    public void OtherQueriesKeepTheirPageLimit()
    {
        var query = new FeatureQuery { Limit = 4, Offset = 8 };

        query.ResolveNearestNeighborRowLimit().Should().Be(4);
        query.NearestNeighborCountExceedsLimit().Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(5, 5)]
    [InlineData(8, 2)]
    [InlineData(10, 0)]
    [InlineData(12, 0)]
    public void LaterPagesStopAtTheNearestCount(int offset, int expectedRows)
    {
        var query = Nearest(10, 5) with { Offset = offset };

        query.ResolveNearestNeighborRowLimit().Should().Be(expectedRows);
        query.NearestNeighborCountExceedsLimit().Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void AnEarlierPageOfALargeCountExceedsTheLimit()
        => (Nearest(20, 5) with { Offset = 5 }).NearestNeighborCountExceedsLimit().Should().BeTrue();

    private static FeatureQuery Nearest(int nearestCount, int? limit) => new()
    {
        Limit = limit,
        SpatialFilter = SpatialFilter.CreateKnnFilter(Origin, nearestCount)
    };
}
