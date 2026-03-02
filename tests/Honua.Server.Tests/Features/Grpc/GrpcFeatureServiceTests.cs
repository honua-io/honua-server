// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Server.Tests.Features.Grpc;

[Protocol(Protocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcFeatureServiceTests
{
    private readonly IResourceValidator _resourceValidator = Substitute.For<IResourceValidator>();
    private readonly IFeatureReader _featureReader = Substitute.For<IFeatureReader>();
    private readonly IStreamingFeatureStore _streamingStore = Substitute.For<IStreamingFeatureStore>();
    private readonly HonuaFeatureService _sut;

    private static readonly LayerDefinition TestLayer = new(
        Id: 0,
        Name: "test",
        Description: null,
        GeometryType: Core.Features.Catalog.Domain.GeometryType.Point,
        SpatialReference: SpatialReference.WGS84,
        Fields: new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
            new FieldDefinition("name", FieldType.String, Length: 255)
        });

    private static readonly ServiceDefinition TestService = new(
        "test", "test", new[] { TestLayer }, SpatialReference.WGS84);

    public GrpcFeatureServiceTests()
    {
        _sut = new HonuaFeatureService(
            _resourceValidator, _featureReader, _streamingStore,
            NullLogger<HonuaFeatureService>.Instance);

        // Default: valid service/layer
        _resourceValidator
            .ValidateServiceLayerAsync("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success((TestService, TestLayer)));
    }

    [UnitTest]
    [Endpoint("gRPC honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithWhereClause_ReturnsFeatures()
    {
        var features = ImmutableArray.Create(
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
                .Add("name", "A")));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            Where = "name = 'A'"
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.ObjectIdFieldName.Should().Be("objectid");
        response.Features.Should().HaveCount(1);
        response.Features[0].Id.Should().Be(1);
        response.Features[0].Attributes["name"].StringValue.Should().Be("A");
    }

    [UnitTest]
    [Endpoint("gRPC honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_CountOnly_ReturnsCountWithNoFeatures()
    {
        _featureReader.CountAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(42L);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            Where = "1=1",
            ReturnCountOnly = true
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.Count.Should().Be(42);
        response.Features.Should().BeEmpty();
    }

    [UnitTest]
    [Endpoint("gRPC honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_IdsOnly_ReturnsObjectIds()
    {
        var features = ImmutableArray.Create(
            Feature.Create(1, null), Feature.Create(2, null), Feature.Create(3, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(3, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ReturnIdsOnly = true
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.ObjectIds.Should().BeEquivalentTo(new long[] { 1, 2, 3 });
        response.Features.Should().BeEmpty();
    }

    [UnitTest]
    [Endpoint("gRPC honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_ExtentOnly_ReturnsExtent()
    {
        _featureReader.GetExtentAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(FeatureExtent.Create(-10, -20, 30, 40, 4326));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ReturnExtentOnly = true
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.Extent.Should().NotBeNull();
        response.Extent.Xmin.Should().Be(-10);
        response.Extent.Ymin.Should().Be(-20);
        response.Extent.Xmax.Should().Be(30);
        response.Extent.Ymax.Should().Be(40);
        response.Features.Should().BeEmpty();
    }

    [UnitTest]
    [Endpoint("gRPC honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_InvalidService_ThrowsNotFoundRpcException()
    {
        _resourceValidator
            .ValidateServiceLayerAsync("bad", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.NotFound<(ServiceDefinition, LayerDefinition)>(
                "Service 'bad' not found"));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "bad",
            LayerId = 0
        };

        var act = async () => await _sut.QueryFeatures(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [UnitTest]
    [Endpoint("gRPC honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_GrpcDisabled_ThrowsNotFoundRpcException()
    {
        var grpcDisabledService = TestService with
        {
            Metadata = new CatalogMetadata
            {
                EnabledProtocols = [ServiceProtocols.FeatureServer]
            }
        };

        _resourceValidator
            .ValidateServiceLayerAsync("grpc-disabled", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success((grpcDisabledService, TestLayer)));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "grpc-disabled",
            LayerId = 0
        };

        var act = async () => await _sut.QueryFeatures(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Be("Grpc is not enabled for this service.");
    }

    [UnitTest]
    [Endpoint("gRPC honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_ExceededTransferLimit_SetsFlag()
    {
        var features = ImmutableArray.Create(Feature.Create(1, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(100, features, hasMoreResults: true));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ResultRecordCount = 1
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.ExceededTransferLimit.Should().BeTrue();
    }

    [UnitTest]
    [Endpoint("gRPC honua.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_StreamsPages()
    {
        var features = Enumerable.Range(1, 5)
            .Select(i => Feature.Create(i, null))
            .ToAsyncEnumerable();

        _streamingStore.StreamFeaturesAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(features);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            Where = "1=1"
        };

        var writer = new TestServerStreamWriter<Proto.FeaturePage>();
        await _sut.QueryFeaturesStream(request, writer, CreateCallContext());

        writer.Pages.Should().NotBeEmpty();
        writer.Pages.Last().IsLastPage.Should().BeTrue();

        // First page has metadata
        writer.Pages[0].ObjectIdFieldName.Should().Be("objectid");
        writer.Pages[0].GeometryType.Should().Be(Proto.GeometryType.Point);
        writer.Pages[0].Fields.Should().HaveCount(2); // objectid + name (both non-geometry)

        // All features accounted for
        var totalFeatures = writer.Pages.Sum(p => p.Features.Count);
        totalFeatures.Should().Be(5);
    }

    private static TestServerCallContext CreateCallContext()
    {
        return new TestServerCallContext();
    }

    /// <summary>
    /// Minimal ServerCallContext for unit testing gRPC services.
    /// </summary>
    private sealed class TestServerCallContext : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public void Dispose() => _cts.Dispose();
        protected override string MethodCore => "/honua.v1.FeatureService/QueryFeatures";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cts.Token;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Test double for IServerStreamWriter that collects written pages.
    /// </summary>
    private sealed class TestServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Pages { get; } = new();
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Pages.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            Pages.Add(message);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Provides async enumerable conversion for test data.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}
