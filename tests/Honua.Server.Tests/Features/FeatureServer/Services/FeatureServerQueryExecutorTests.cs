// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

public sealed class FeatureServerQueryExecutorTests
{
    [Fact]
    public async Task QueryWithValidationAsync_WhenReaderThrowsArgumentException_ThrowsInvalidOperationException()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueryResult<Feature>>(new ArgumentException("Invalid where clause")));

        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryWithValidationAsync(1, default, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid query:*");
    }

    [Fact]
    public async Task QueryWithValidationAsync_WhenReaderThrowsSqlWordedException_PropagatesOriginalException()
    {
        var expected = new TimeoutException("SQL connection dropped unexpectedly");
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueryResult<Feature>>(expected));

        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryWithValidationAsync(1, default, CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<TimeoutException>();
        thrown.Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task QueryFlatGeobufWithValidationAsync_WhenReaderThrowsArgumentException_ThrowsInvalidOperationException()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.QueryFlatGeobufAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]?>(new ArgumentException("Invalid where clause")));

        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryFlatGeobufWithValidationAsync(1, default, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid query:*");
    }

    [Fact]
    public async Task QueryFlatGeobufWithValidationAsync_WhenReaderThrowsSqlWordedException_PropagatesOriginalException()
    {
        var expected = new TimeoutException("SQL connection dropped unexpectedly");
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.QueryFlatGeobufAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]?>(expected));

        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryFlatGeobufWithValidationAsync(1, default, CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<TimeoutException>();
        thrown.Which.Should().BeSameAs(expected);
    }

    private static FeatureServerQueryExecutor CreateSut(IFeatureReader featureReader)
    {
        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        var formatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));

        return new FeatureServerQueryExecutor(featureReader, streamingStore, formatter);
    }
}
