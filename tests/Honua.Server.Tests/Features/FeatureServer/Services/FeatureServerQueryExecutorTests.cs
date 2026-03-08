// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Collections.Immutable;
using System.Text.Json;

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

    [Fact]
    public async Task StreamQueryAsync_WithPagedQuery_UsesLimitProbeInsteadOfCount()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(new InvalidOperationException("CountAsync should not be called for paged streaming queries.")));

        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        FeatureQuery? capturedQuery = null;
        streamingStore.StreamFeaturesAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedQuery = callInfo.ArgAt<FeatureQuery>(1);
                return (IAsyncEnumerable<Feature>)StreamFeatures(
                [
                    CreateFeature(1, "alpha"),
                    CreateFeature(2, "beta"),
                    CreateFeature(3, "gamma")
                ]);
            });

        var sut = CreateSut(featureReader, streamingStore);
        var context = CreateHttpContext();

        await sut.StreamQueryAsync(
            7,
            new FeatureQuery { Limit = 2 },
            CreateLayer(),
            new QueryParameters { F = "geojson", ReturnGeometry = false },
            outputSrid: null,
            context,
            CancellationToken.None);

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.Limit.Should().Be(3);
        featureReader.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == nameof(IFeatureReader.CountAsync));

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("features").GetArrayLength().Should().Be(2);
        document.RootElement.GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task StreamQueryAsync_WithPagedQueryThatFits_DoesNotSetExceededTransferLimit()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(new InvalidOperationException("CountAsync should not be called for paged streaming queries.")));

        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        streamingStore.StreamFeaturesAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IAsyncEnumerable<Feature>)StreamFeatures(
            [
                CreateFeature(1, "alpha"),
                CreateFeature(2, "beta")
            ]));

        var sut = CreateSut(featureReader, streamingStore);
        var context = CreateHttpContext();

        await sut.StreamQueryAsync(
            7,
            new FeatureQuery { Limit = 2 },
            CreateLayer(),
            new QueryParameters { F = "json", ReturnGeometry = false },
            outputSrid: null,
            context,
            CancellationToken.None);

        featureReader.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == nameof(IFeatureReader.CountAsync));

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("features").GetArrayLength().Should().Be(2);
        document.RootElement.TryGetProperty("exceededTransferLimit", out _).Should().BeFalse();
    }

    private static FeatureServerQueryExecutor CreateSut(
        IFeatureReader featureReader,
        IStreamingFeatureStore? streamingStore = null)
    {
        streamingStore ??= Substitute.For<IStreamingFeatureStore>();
        var formatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));

        return new FeatureServerQueryExecutor(featureReader, streamingStore, formatter);
    }

    private static LayerDefinition CreateLayer()
        => new(
            7,
            "test-layer",
            null,
            GeometryType.None,
            SpatialReference.WGS84,
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ]);

    private static Feature CreateFeature(long id, string name)
        => Feature.Create(
            id,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", name));

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Protocol = "HTTP/1.1";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async IAsyncEnumerable<Feature> StreamFeatures(IEnumerable<Feature> features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }
    }
}
