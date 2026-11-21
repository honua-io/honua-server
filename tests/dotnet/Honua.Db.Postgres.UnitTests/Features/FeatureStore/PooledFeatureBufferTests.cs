// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class PooledFeatureBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(500)]
    public void ToImmutable_AllRows_KeepsReadOrderAndExactLength(int rowCount)
    {
        using var buffer = Fill(rowCount);

        var features = buffer.ToImmutable(buffer.Count);

        features.Length.Should().Be(rowCount);
        features.Select(feature => feature.Id).Should().Equal(Enumerable.Range(1, rowCount).Select(id => (long)id));
    }

    [Fact]
    public void ToImmutable_ProbeRowDropped_ReturnsLeadingRowsOnly()
    {
        using var buffer = Fill(101);

        var page = buffer.ToImmutable(100);

        page.Length.Should().Be(100);
        page[^1].Id.Should().Be(100);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void ToImmutable_CountOutsideRows_Throws(int count)
    {
        using var buffer = Fill(3);

        var act = () => buffer.ToImmutable(count);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToImmutable_AfterDispose_ReturnedArrayIsUnaffected()
    {
        var buffer = Fill(70);
        var features = buffer.ToImmutable(buffer.Count);

        buffer.Dispose();
        buffer.Dispose();

        buffer.Count.Should().Be(0);
        features.Should().HaveCount(70);
        features[69].Id.Should().Be(70);
    }

    private static PooledFeatureBuffer Fill(int rowCount)
    {
        var buffer = new PooledFeatureBuffer();
        for (var id = 1; id <= rowCount; id++)
        {
            buffer.Add(Feature.Create(id, null));
        }

        return buffer;
    }
}
