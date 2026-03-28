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
using Npgsql;
using NSubstitute;
using System.Collections.Immutable;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

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
            .WithMessage("Invalid query.");
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
            .WithMessage("Invalid query.");
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
    public async Task QueryFlatGeobufWithValidationAsync_WhenReaderThrowsNpgsqlException_ThrowsInvalidOperationException()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.QueryFlatGeobufAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]?>(new NpgsqlException("Connection dropped")));

        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryFlatGeobufWithValidationAsync(1, default, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Query execution failed.");
    }

    [Fact]
    public async Task QueryGeobufWithValidationAsync_WhenReaderThrowsArgumentException_ThrowsInvalidOperationException()
    {
        var featureReader = Substitute.For<IFeatureReader, IGeobufFeatureStore>();
        ((IGeobufFeatureStore)featureReader).QueryGeobufAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]?>(new ArgumentException("Invalid where clause")));

        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryGeobufWithValidationAsync(1, default, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid query.");
    }

    [Fact]
    public async Task QueryGeobufWithValidationAsync_WhenStoreNotSupported_ThrowsInvalidOperationException()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryGeobufWithValidationAsync(1, default, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("Geobuf output is not supported by the configured feature store.");
    }

    [Fact]
    public async Task QueryGeobufWithValidationAsync_WhenReaderThrowsNpgsqlException_ThrowsInvalidOperationException()
    {
        var featureReader = Substitute.For<IFeatureReader, IGeobufFeatureStore>();
        ((IGeobufFeatureStore)featureReader).QueryGeobufAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]?>(new NpgsqlException("Connection dropped")));

        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryGeobufWithValidationAsync(1, default, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Query execution failed.");
    }

    [Fact]
    public async Task QueryWithValidationAsync_WhenReaderThrowsNpgsqlException_ThrowsInvalidOperationException()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueryResult<Feature>>(new NpgsqlException("Connection dropped")));

        var sut = CreateSut(featureReader);

        Func<Task> act = () => sut.QueryWithValidationAsync(1, default, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Query execution failed.");
    }

    [Fact]
    public async Task QueryWithValidationAsync_WhenPagedQuerySupported_UsesPagedFastPath()
    {
        var featureReader = Substitute.For<IFeatureReader, IPagedFeatureReader>();
        var expectedItems = ImmutableArray.Create(CreateFeature(1, "alpha"), CreateFeature(2, "beta"));
        ((IPagedFeatureReader)featureReader)
            .QueryPageAsync(5, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PagedQueryResult<Feature>.Create(expectedItems, hasMoreResults: true)));

        var sut = CreateSut(featureReader);
        var query = new FeatureQuery { Limit = 2 };

        var result = await sut.QueryWithValidationAsync(5, query, CancellationToken.None);

        result.Items.Should().BeEquivalentTo(expectedItems);
        result.HasMoreResults.Should().BeTrue();
        result.TotalCount.Should().Be(3);
        await ((IPagedFeatureReader)featureReader).Received(1).QueryPageAsync(5, query, Arg.Any<CancellationToken>());
        await featureReader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryWithValidationAsync_WhenPagedQueryNotSupported_FallsBackToRegularQuery()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var expected = QueryResult<Feature>.Create(
            totalCount: 2,
            items: ImmutableArray.Create(CreateFeature(1, "alpha"), CreateFeature(2, "beta")));
        featureReader.QueryAsync(5, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        var sut = CreateSut(featureReader);
        var query = new FeatureQuery { Limit = 2 };

        var result = await sut.QueryWithValidationAsync(5, query, CancellationToken.None);

        result.Should().Be(expected);
        await featureReader.Received(1).QueryAsync(5, query, Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task StreamQueryAsync_WithOutputSrid_SetsFeatureGeometrySpatialReference()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        streamingStore.StreamFeaturesAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IAsyncEnumerable<Feature>)StreamFeatures(
            [
                CreateFeature(1, "alpha", CreatePointGeometry(1, 2, 4326))
            ]));

        var sut = CreateSut(featureReader, streamingStore);
        var context = CreateHttpContext();

        await sut.StreamQueryAsync(
            7,
            new FeatureQuery { Limit = 1 },
            CreatePointLayer(),
            new QueryParameters { F = "json", ReturnGeometry = true },
            outputSrid: 3857,
            context,
            CancellationToken.None);

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        var geometry = document.RootElement
            .GetProperty("features")[0]
            .GetProperty("geometry");
        geometry.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
    }

    [Fact]
    public async Task StreamQueryAsync_WithGeoJson_UsesSharedIdAndObjectIdProperties()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        streamingStore.StreamFeaturesAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IAsyncEnumerable<Feature>)StreamFeatures(
            [
                CreateFeature(42, "alpha")
            ]));

        var sut = CreateSut(featureReader, streamingStore);
        var context = CreateHttpContext();

        await sut.StreamQueryAsync(
            7,
            new FeatureQuery { Limit = 1 },
            CreateLayer(),
            new QueryParameters { F = "geojson", ReturnGeometry = false, OutFields = "name" },
            outputSrid: null,
            context,
            CancellationToken.None);

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        var feature = document.RootElement.GetProperty("features")[0];
        feature.GetProperty("id").GetInt64().Should().Be(42);
        var properties = feature.GetProperty("properties");
        properties.GetProperty("objectid").GetInt64().Should().Be(42);
        properties.GetProperty("name").GetString().Should().Be("alpha");
        properties.TryGetProperty("OBJECTID", out _).Should().BeFalse();
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
            Honua.Core.Features.Catalog.Domain.GeometryType.None,
            SpatialReference.WGS84,
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ]);

    private static LayerDefinition CreatePointLayer()
        => new(
            7,
            "test-layer",
            null,
            Honua.Core.Features.Catalog.Domain.GeometryType.Point,
            SpatialReference.WGS84,
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ]);

    private static Feature CreateFeature(long id, string name, byte[]? geometry = null)
        => Feature.Create(
            id,
            geometry,
            ImmutableDictionary<string, object?>.Empty.Add("name", name));

    private static byte[] CreatePointGeometry(double x, double y, int srid)
    {
        var writer = new WKBWriter();
        var point = new Point(x, y) { SRID = srid };
        return writer.Write(point);
    }

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
