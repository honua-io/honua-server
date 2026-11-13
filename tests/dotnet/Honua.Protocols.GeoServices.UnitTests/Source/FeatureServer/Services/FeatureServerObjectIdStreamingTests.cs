// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

// Regression guard: returnIdsOnly over a large layer must write ids while it reads them. A path that
// collects the ids first lets the whole layer go by before the response body grows.
public sealed class FeatureServerObjectIdStreamingTests
{
    private const int LayerId = 7;
    private const int LayerFeatureCount = 100_000;
    private const int MaxIdsBetweenBodyWrites = 8_192;

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task StreamIdsAsync_HundredThousandIds_WritesBodyWhileReading()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var source = new BodyWatchingIdSource(body);
        var reader = Substitute.For<IFeatureReader, IObjectIdStreamingReader>();
        ((IObjectIdStreamingReader)reader)
            .StreamObjectIdsAsync(LayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(source.Ids(LayerFeatureCount));
        var sut = new FeatureServerQueryExecutor(
            reader,
            Substitute.For<IStreamingFeatureStore>(),
            new StreamingQueryFormatter(Options.Create(new LimitsOptions())));

        await sut.StreamIdsAsync(LayerId, new FeatureQuery(), "objectid", context, CancellationToken.None);

        source.LongestRunWithoutBodyWrite.Should().BeLessThan(MaxIdsBetweenBodyWrites);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        var objectIds = document.RootElement.GetProperty("objectIds");
        objectIds.GetArrayLength().Should().Be(LayerFeatureCount);
        objectIds[0].GetInt64().Should().Be(1);
        objectIds[LayerFeatureCount - 1].GetInt64().Should().Be(LayerFeatureCount);
        await reader.DidNotReceive().QueryObjectIdsAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task StreamIdsAsync_InvalidQueryOnFirstRead_ThrowsBeforeWritingStatus()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var reader = Substitute.For<IFeatureReader, IObjectIdStreamingReader>();
        ((IObjectIdStreamingReader)reader)
            .StreamObjectIdsAsync(LayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(FailingIds());
        var sut = new FeatureServerQueryExecutor(
            reader,
            Substitute.For<IStreamingFeatureStore>(),
            new StreamingQueryFormatter(Options.Create(new LimitsOptions())));

        var act = () => sut.StreamIdsAsync(LayerId, new FeatureQuery(), "objectid", context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Invalid query.");
        context.Response.ContentType.Should().BeNull();
        context.Response.Body.Length.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task StreamIdsAsync_DatabaseErrorWithinFirstPage_ThrowsBeforeWritingStatus()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var reader = Substitute.For<IFeatureReader, IObjectIdStreamingReader>();
        ((IObjectIdStreamingReader)reader)
            .StreamObjectIdsAsync(LayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(IdsThenFailure(failAfter: 1_000));
        var sut = new FeatureServerQueryExecutor(
            reader,
            Substitute.For<IStreamingFeatureStore>(),
            new StreamingQueryFormatter(Options.Create(new LimitsOptions())));

        var act = () => sut.StreamIdsAsync(LayerId, new FeatureQuery(), "objectid", context, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        context.Response.ContentType.Should().BeNull();
        context.Response.Body.Length.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task StreamIdsAsync_DatabaseErrorAfterResponseStarted_AbortsTheConnection()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        context.Features.Set<IHttpResponseFeature>(new StartedOnFirstWriteResponseFeature(body));
        var lifetime = new RecordingLifetimeFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var reader = Substitute.For<IFeatureReader, IObjectIdStreamingReader>();
        ((IObjectIdStreamingReader)reader)
            .StreamObjectIdsAsync(LayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(IdsThenFailure(failAfter: 10_000));
        var sut = new FeatureServerQueryExecutor(
            reader,
            Substitute.For<IStreamingFeatureStore>(),
            new StreamingQueryFormatter(Options.Create(new LimitsOptions())));

        var act = () => sut.StreamIdsAsync(LayerId, new FeatureQuery(), "objectid", context, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        body.Length.Should().BePositive();
        lifetime.Aborted.Should().BeTrue();
    }

    private static async IAsyncEnumerable<long> FailingIds()
    {
        await Task.Yield();
        yield return RejectQuery();
    }

    private static long RejectQuery() => throw new ArgumentException("unknown field");

    private static async IAsyncEnumerable<long> IdsThenFailure(int failAfter)
    {
        await Task.Yield();
        for (var id = 1L; id <= failAfter; id++)
        {
            yield return id;
        }

        yield return FailStatement();
    }

    private static long FailStatement() => throw new TimeoutException("statement timeout");

    private sealed class BodyWatchingIdSource(MemoryStream body)
    {
        private const int YieldEvery = 4_096;

        public int LongestRunWithoutBodyWrite { get; private set; }

        public async IAsyncEnumerable<long> Ids(int count)
        {
            var lastLength = body.Length;
            var run = 0;
            for (var id = 1L; id <= count; id++)
            {
                run = body.Length == lastLength ? run + 1 : 0;
                lastLength = body.Length;
                LongestRunWithoutBodyWrite = Math.Max(LongestRunWithoutBodyWrite, run);
                if (id % YieldEvery == 0)
                {
                    await Task.Yield();
                }

                yield return id;
            }
        }
    }
}
