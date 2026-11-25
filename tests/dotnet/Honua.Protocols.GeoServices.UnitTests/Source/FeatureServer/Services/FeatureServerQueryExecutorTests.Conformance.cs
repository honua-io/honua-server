// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class FeatureServerQueryExecutorTests
{
    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public async Task QueryWithValidationAsync_ZeroLimitWithPagedReader_UsesPagedQueryAndPropagatesHasMoreResults()
    {
        var reader = Substitute.For<IFeatureReader, IPagedFeatureReader>();
        ((IPagedFeatureReader)reader)
            .QueryPageAsync(7, Arg.Is<FeatureQuery>(q => q.Limit == 0), Arg.Any<CancellationToken>())
            .Returns(PagedQueryResult<Feature>.Create([], hasMoreResults: true));

        var sut = CreateSut(reader);
        var result = await sut.QueryWithValidationAsync(7, new FeatureQuery { Limit = 0 }, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.HasMoreResults.Should().BeTrue();
        await reader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData(2, new long[] { 10, 20, 30 }, new long[] { 10, 20 })]
    [InlineData(0, new long[] { 10 }, new long[] { })]
    public async Task QueryObjectIdsWithLimitProbeAsync_WhenMoreIdsExist_TrimsAndSetsExceededTransferLimit(
        int limit,
        long[] readerIds,
        long[] expectedIds)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryObjectIdsAsync(
                7,
                Arg.Is<FeatureQuery>(q => q.Limit == limit + 1 && q.ReturnPublicObjectIds),
                Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(readerIds));

        var sut = CreateSut(reader);
        var result = await sut.QueryObjectIdsWithLimitProbeAsync(7, new FeatureQuery { Limit = limit }, CancellationToken.None);

        result.ObjectIds.Should().Equal(expectedIds);
        result.ExceededTransferLimit.Should().BeTrue();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData(2, new long[] { 10, 20 })]
    [InlineData(0, new long[] { })]
    public async Task QueryObjectIdsWithLimitProbeAsync_WhenWithinLimit_DoesNotSetExceededTransferLimit(
        int limit,
        long[] readerIds)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryObjectIdsAsync(
                7,
                Arg.Is<FeatureQuery>(q => q.Limit == limit + 1 && q.ReturnPublicObjectIds),
                Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(readerIds));

        var sut = CreateSut(reader);
        var result = await sut.QueryObjectIdsWithLimitProbeAsync(7, new FeatureQuery { Limit = limit }, CancellationToken.None);

        result.ObjectIds.Should().Equal(readerIds);
        result.ExceededTransferLimit.Should().BeFalse();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public async Task QueryObjectIdsWithLimitProbeAsync_WhenLimitIsNull_ProbesMaxNonStreamingObjectIds()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryObjectIdsAsync(
                7,
                Arg.Is<FeatureQuery>(q => q.Limit == FeatureServerQueryExecutor.MaxNonStreamingObjectIds + 1 && q.ReturnPublicObjectIds),
                Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(1L, 2L));

        var sut = CreateSut(reader);
        var result = await sut.QueryObjectIdsWithLimitProbeAsync(7, new FeatureQuery { Limit = null }, CancellationToken.None);

        result.ObjectIds.Should().Equal(1L, 2L);
        result.ExceededTransferLimit.Should().BeFalse();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData(2, 3)]
    [InlineData(0, 1)]
    [InlineData(4100, 4101)]
    public async Task StreamIdsAsync_StreamingReaderExceedingLimit_SetsExceededTransferLimit(
        int limit,
        int totalMatchingIds)
    {
        var (observedLimit, count, exceeded) = await RunStreamingIdsAsync(limit, totalMatchingIds);

        observedLimit.Should().Be(limit + 1);
        count.Should().Be(limit);
        exceeded.Should().BeTrue();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    [InlineData(4100, 4100)]
    public async Task StreamIdsAsync_StreamingReaderWithinLimit_ClearsExceededTransferLimit(
        int limit,
        int totalMatchingIds)
    {
        var (observedLimit, count, exceeded) = await RunStreamingIdsAsync(limit, totalMatchingIds);

        observedLimit.Should().Be(limit + 1);
        count.Should().Be(totalMatchingIds);
        exceeded.Should().BeFalse();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public async Task StreamIdsAsync_NonStreamingFallbackWithLimit_EmitsExceededTransferLimit()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryObjectIdsAsync(
                7,
                Arg.Is<FeatureQuery>(q => q.Limit == 3 && q.ReturnPublicObjectIds),
                Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(1L, 2L, 3L));

        var sut = CreateSut(reader);
        var context = CreateHttpContext();

        await sut.StreamIdsAsync(7, new FeatureQuery { Limit = 2 }, "objectid", context, CancellationToken.None);

        using var doc = JsonDocument.Parse(await ReadResponseAsync(context));
        doc.RootElement.GetProperty("objectIds").EnumerateArray().Select(static e => e.GetInt64()).Should().Equal(1L, 2L);
        doc.RootElement.GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue();
    }

    private async Task<(int? ObservedLimit, int Count, bool Exceeded)> RunStreamingIdsAsync(int limit, int totalMatchingIds)
    {
        int? observedLimit = null;
        var reader = Substitute.For<IFeatureReader, IObjectIdStreamingReader>();
        ((IObjectIdStreamingReader)reader)
            .StreamObjectIdsAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var query = callInfo.ArgAt<FeatureQuery>(1);
                observedLimit = query.Limit;
                return StreamObjectIds(totalMatchingIds, query.Limit);
            });
        var sut = CreateSut(reader);
        var context = CreateHttpContext();

        await sut.StreamIdsAsync(7, new FeatureQuery { Limit = limit }, "objectid", context, CancellationToken.None);

        using var doc = JsonDocument.Parse(await ReadResponseAsync(context));
        return (
            observedLimit,
            doc.RootElement.GetProperty("objectIds").GetArrayLength(),
            doc.RootElement.GetProperty("exceededTransferLimit").GetBoolean());
    }

    private static async IAsyncEnumerable<long> StreamObjectIds(
        int totalMatchingIds,
        int? limit,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var maxToYield = limit is int cap ? Math.Min(totalMatchingIds, cap) : totalMatchingIds;
        for (var i = 1; i <= maxToYield; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return i;
        }
    }
}
