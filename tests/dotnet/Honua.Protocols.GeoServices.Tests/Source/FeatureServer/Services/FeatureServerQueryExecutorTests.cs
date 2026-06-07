// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using System.Collections.Immutable;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class FeatureServerQueryExecutorTests
{
    [Fact]
    public async Task CountAsync_WhenLayerMappingIsNotSourceBacked_UsesCanonicalFeatureReader()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.CountAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(3L);
        var providerReader = Substitute.For<IFeatureReader>();
        var router = CreateProviderRouter(providerReader);
        var sut = CreateSut(featureReader, providerQueryRouter: router);
        var service = CreateService();
        var resource = CreatePointResource();
        var publication = CreatePublication(service, resource);

        var count = await sut.CountAsync(service, resource, publication, 7, new FeatureQuery(), CancellationToken.None);

        count.Should().Be(3L);
        await providerReader.DidNotReceive()
            .CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CountAsync_WhenLayerMappingIsSourceBackedWithoutConnection_UsesProviderReader()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var providerReader = Substitute.For<IFeatureReader>();
        providerReader.CountAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(5L);
        var router = CreateProviderRouter(providerReader);
        var service = CreateService();
        var resource = CreatePointResource(storageBindingIds: ["binding-roads"]);
        var publication = CreatePublication(service, resource, storageBindingId: "binding-roads");
        var graphProvider = CreateGraphProvider(
            service,
            resource,
            publication,
            CreateStorageBinding(resource, "binding-roads"));
        var sut = CreateSut(featureReader, providerQueryRouter: router, metadataGraphProvider: graphProvider);

        var count = await sut.CountAsync(service, resource, publication, 7, new FeatureQuery(), CancellationToken.None);

        count.Should().Be(5L);
        await featureReader.DidNotReceive()
            .CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

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
    public async Task QueryRawGeoServicesPointJsonWithValidationAsync_SanitizesStoredAttributesAndInjectsObjectId()
    {
        var featureReader = Substitute.For<IFeatureReader, IPagedRawGeoServicesFeatureStore>();
        var rawStore = (IPagedRawGeoServicesFeatureStore)featureReader;
        var rawFeatures = ImmutableArray.Create(
            RawGeoServicesFeature.Create(
                42,
                "123",
                """{"id":999,"name":"alpha","extra":"leak","__internal":"secret"}""",
                1.5,
                2.5));
        rawStore.QueryGeoServicesRawPointPageAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PagedQueryResult<RawGeoServicesFeature>.Create(rawFeatures)));

        var sut = CreateSut(featureReader);
        var service = CreateService();
        var resource = CreatePointResourceWithCustomObjectId();
        var publication = CreatePublication(service, resource);

        var (payload, count) = await sut.QueryRawGeoServicesPointJsonWithValidationAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 1 },
            returnGeometry: true,
            outputSrid: null,
            cancellationToken: CancellationToken.None);

        count.Should().Be(1);
        using var document = JsonDocument.Parse(payload);
        var feature = document.RootElement.GetProperty("features")[0];
        var attributes = feature.GetProperty("attributes");
        attributes.GetProperty("id").GetInt64().Should().Be(123);
        attributes.GetProperty("name").GetString().Should().Be("alpha");
        attributes.TryGetProperty("extra", out _).Should().BeFalse();
        attributes.TryGetProperty("__internal", out _).Should().BeFalse();
        feature.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(1.5);
        feature.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(2.5);
    }

    [Fact]
    public async Task QueryRawGeoServicesPointJsonWithValidationAsync_WithNoStoredAttributes_InjectsInternalObjectId()
    {
        var featureReader = Substitute.For<IFeatureReader, IPagedRawGeoServicesFeatureStore>();
        var rawStore = (IPagedRawGeoServicesFeatureStore)featureReader;
        var rawFeatures = ImmutableArray.Create(RawGeoServicesFeature.Create(42, attributesJson: null, x: 1.5, y: 2.5));
        rawStore.QueryGeoServicesRawPointPageAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PagedQueryResult<RawGeoServicesFeature>.Create(rawFeatures)));

        var sut = CreateSut(featureReader);
        var service = CreateService();
        var resource = CreatePointResource();
        var publication = CreatePublication(service, resource);

        var (payload, count) = await sut.QueryRawGeoServicesPointJsonWithValidationAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 1 },
            returnGeometry: true,
            outputSrid: null,
            cancellationToken: CancellationToken.None);

        count.Should().Be(1);
        using var document = JsonDocument.Parse(payload);
        var attributes = document.RootElement
            .GetProperty("features")[0]
            .GetProperty("attributes");
        attributes.GetProperty(FieldNames.ObjectId).GetInt64().Should().Be(42);
    }

    [Fact]
    public async Task QueryRawGeoServicesPointJsonWithValidationAsync_WhenReturnGeometryFalse_OmitsGeometry()
    {
        var featureReader = Substitute.For<IFeatureReader, IPagedRawGeoServicesFeatureStore>();
        var rawStore = (IPagedRawGeoServicesFeatureStore)featureReader;
        var rawFeatures = ImmutableArray.Create(
            RawGeoServicesFeature.Create(
                42,
                "123",
                """{"id":999,"name":"alpha"}""",
                1.5,
                2.5));
        rawStore.QueryGeoServicesRawPointPageAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PagedQueryResult<RawGeoServicesFeature>.Create(rawFeatures)));

        var sut = CreateSut(featureReader);
        var service = CreateService();
        var resource = CreatePointResourceWithCustomObjectId();
        var publication = CreatePublication(service, resource);

        var (payload, count) = await sut.QueryRawGeoServicesPointJsonWithValidationAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 1 },
            returnGeometry: false,
            outputSrid: null,
            cancellationToken: CancellationToken.None);

        count.Should().Be(1);
        using var document = JsonDocument.Parse(payload);
        var feature = document.RootElement.GetProperty("features")[0];
        feature.GetProperty("attributes").GetProperty("id").GetInt64().Should().Be(123);
        feature.TryGetProperty("geometry", out _).Should().BeFalse();
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
        var service = CreateService();
        var resource = CreateResource();
        var publication = CreatePublication(service, resource);

        await sut.StreamQueryAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 2 },
            new QueryParameters { F = "geojson", ReturnGeometry = false },
            outputSrid: null,
            context: context,
            cancellationToken: CancellationToken.None);

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.Limit.Should().Be(3);
        featureReader.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == nameof(IFeatureReader.CountAsync));

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("features").GetArrayLength().Should().Be(2);
        document.RootElement.GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task StreamQueryAsync_WithPagedQueryThatFits_EmitsExceededTransferLimitFalse()
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
        var service = CreateService();
        var resource = CreateResource();
        var publication = CreatePublication(service, resource);

        await sut.StreamQueryAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 2 },
            new QueryParameters { F = "json", ReturnGeometry = false },
            outputSrid: null,
            context: context,
            cancellationToken: CancellationToken.None);

        featureReader.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == nameof(IFeatureReader.CountAsync));

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("features").GetArrayLength().Should().Be(2);
        // Esri's query contract always emits exceededTransferLimit, including the false case.
        document.RootElement.TryGetProperty("exceededTransferLimit", out var exceeded).Should().BeTrue();
        exceeded.GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task StreamQueryAsync_WithOutputSrid_SetsTopLevelSpatialReferenceOnly()
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
        var service = CreateService();
        var resource = CreatePointResource();
        var publication = CreatePublication(service, resource);

        await sut.StreamQueryAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 1 },
            new QueryParameters { F = "json", ReturnGeometry = true },
            outputSrid: 3857,
            context: context,
            cancellationToken: CancellationToken.None);

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        document.RootElement
            .GetProperty("spatialReference")
            .GetProperty("wkid")
            .GetInt32()
            .Should()
            .Be(3857);
        document.RootElement
            .GetProperty("features")[0]
            .GetProperty("geometry")
            .TryGetProperty("spatialReference", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task StreamQueryAsync_WithPointGeometryPrecision_RoundsCoordinates()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        streamingStore.StreamFeaturesAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IAsyncEnumerable<Feature>)StreamFeatures(
            [
                CreateFeature(1, "alpha", CreatePointGeometry(1.1234567, 2.7654321, 4326))
            ]));

        var sut = CreateSut(featureReader, streamingStore);
        var context = CreateHttpContext();
        var service = CreateService();
        var resource = CreatePointResource();
        var publication = CreatePublication(service, resource);

        await sut.StreamQueryAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 1 },
            new QueryParameters { F = "json", ReturnGeometry = true, GeometryPrecision = 2 },
            outputSrid: 4326,
            context: context,
            cancellationToken: CancellationToken.None);

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        var geometry = document.RootElement
            .GetProperty("features")[0]
            .GetProperty("geometry");
        geometry.GetProperty("x").GetDouble().Should().Be(1.12);
        geometry.GetProperty("y").GetDouble().Should().Be(2.77);
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
        var service = CreateService();
        var resource = CreateResource();
        var publication = CreatePublication(service, resource);

        await sut.StreamQueryAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 1 },
            new QueryParameters { F = "geojson", ReturnGeometry = false, OutFields = "name" },
            outputSrid: null,
            context: context,
            cancellationToken: CancellationToken.None);

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        var feature = document.RootElement.GetProperty("features")[0];
        feature.GetProperty("id").GetInt64().Should().Be(42);
        var properties = feature.GetProperty("properties");
        properties.GetProperty("objectid").GetInt64().Should().Be(42);
        properties.GetProperty("name").GetString().Should().Be("alpha");
        properties.TryGetProperty("OBJECTID", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StreamQueryAsync_WithGeoJson_AllFields_PreservesObjectIdAlias()
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
        var service = CreateService();
        var resource = CreateResource();
        var publication = CreatePublication(service, resource);

        await sut.StreamQueryAsync(
            service,
            resource,
            publication,
            7,
            new FeatureQuery { Limit = 1 },
            new QueryParameters { F = "geojson", ReturnGeometry = false },
            outputSrid: null,
            context: context,
            cancellationToken: CancellationToken.None);

        var json = await ReadResponseAsync(context);
        using var document = JsonDocument.Parse(json);
        var feature = document.RootElement.GetProperty("features")[0];
        feature.GetProperty("id").GetInt64().Should().Be(42);
        var properties = feature.GetProperty("properties");
        properties.GetProperty("objectid").GetInt64().Should().Be(42);
        // GeoJSON properties mirror the f=json attributes (lowercase objectid only);
        // the synthetic uppercase OBJECTID alias is intentionally suppressed (#1518).
        properties.TryGetProperty("OBJECTID", out _).Should().BeFalse();
        properties.GetProperty("name").GetString().Should().Be("alpha");
    }

    private static FeatureServerQueryExecutor CreateSut(
        IFeatureReader featureReader,
        IStreamingFeatureStore? streamingStore = null,
        FeatureProviderQueryRouter? providerQueryRouter = null,
        IMetadataV2GraphProvider? metadataGraphProvider = null)
    {
        streamingStore ??= Substitute.For<IStreamingFeatureStore>();
        var formatter = new StreamingQueryFormatter(Options.Create(new LimitsOptions()));

        return new FeatureServerQueryExecutor(
            featureReader,
            streamingStore,
            formatter,
            providerQueryRouter,
            metadataGraphProvider);
    }

    private static FeatureProviderQueryRouter CreateProviderRouter(IFeatureReader reader)
    {
        var provider = Substitute.For<IFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        provider.Reader.Returns(reader);
        var providerRegistry = new FeatureDataProviderRegistry([provider]);
        var connectionRegistry = Substitute.For<ISecureConnectionRegistry>();

        return new FeatureProviderQueryRouter(connectionRegistry, providerRegistry);
    }

    private static MetadataV2Service CreateService()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "maps", Name = "Maps" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };

    private static MetadataV2Publication CreatePublication(
        MetadataV2Service service,
        MetadataV2Resource resource,
        string? storageBindingId = null)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"{service.Metadata.Id}-{resource.Metadata.Id}", Name = resource.Metadata.Name },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = storageBindingId,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = "7",
                IsNumeric = true
            },
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer
        };

    private static TestMetadataV2GraphProvider CreateGraphProvider(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        MetadataV2StorageBinding? storageBinding = null)
        => new(new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = [resource],
            Services = [service],
            Publications = [publication],
            StorageBindings = storageBinding is null ? [] : [storageBinding]
        });

    private static MetadataV2StorageBinding CreateStorageBinding(
        MetadataV2Resource resource,
        string id,
        int storageLayerId = 7)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            ResourceId = resource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "public.roads",
            StorageLayerId = storageLayerId
        };

    private static MetadataV2Resource CreateResource(IReadOnlyList<string>? storageBindingIds = null)
        => CreateResource(
            "test-layer",
            MetadataV2GeometryType.None,
            storageBindingIds,
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 });

    private static MetadataV2Resource CreatePointResource(IReadOnlyList<string>? storageBindingIds = null)
        => CreateResource(
            "test-layer",
            MetadataV2GeometryType.Point,
            storageBindingIds,
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 });

    private static MetadataV2Resource CreatePointResourceWithCustomObjectId()
        => CreateResource(
            "test-layer",
            MetadataV2GeometryType.Point,
            null,
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 });

    private static MetadataV2Resource CreateResource(
        string name,
        MetadataV2GeometryType geometryType,
        IReadOnlyList<string>? storageBindingIds,
        params MetadataV2Field[] fields)
    {
        var schemaFields = new List<MetadataV2Field>(fields);
        MetadataV2ResourceSpatial? spatial = null;
        if (geometryType is not MetadataV2GeometryType.None)
        {
            schemaFields.Add(new MetadataV2Field
            {
                Name = "shape",
                Type = MetadataV2FieldType.Geometry,
                Nullable = true,
                SemanticRoles = ["geometry.primary"]
            });
            spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = geometryType,
                PrimaryGeometryField = "shape"
            };
        }

        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = name, Name = name },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = storageBindingIds ?? [],
            SchemaFields = schemaFields,
            Spatial = spatial
        };
    }

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
