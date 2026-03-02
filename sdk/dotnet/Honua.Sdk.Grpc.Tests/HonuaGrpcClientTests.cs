// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Moq;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Sdk.Grpc.Tests;

public class HonuaGrpcClientTests
{
    [Fact]
    public async Task QueryFeaturesAsync_DelegatesToStub()
    {
        var protoResponse = new Proto.QueryFeaturesResponse
        {
            ObjectIdFieldName = "OBJECTID",
            GeometryType = Proto.GeometryType.Point,
        };

        var mockClient = new Mock<Proto.FeatureService.FeatureServiceClient>();
        mockClient
            .Setup(c => c.QueryFeaturesAsync(
                It.IsAny<Proto.QueryFeaturesRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncUnaryCall(protoResponse));

        var client = new HonuaGrpcClient(mockClient.Object);
        var request = new Models.QueryFeaturesRequest
        {
            ServiceId = "test-svc",
            LayerId = 0,
        };

        var result = await client.QueryFeaturesAsync(request);

        Assert.Equal("OBJECTID", result.ObjectIdFieldName);
        Assert.Equal(Models.GeometryType.Point, result.GeometryType);
    }

    [Fact]
    public async Task QueryFeaturesStreamAsync_YieldsPagesAndStopsOnLastPage()
    {
        var page1 = new Proto.FeaturePage
        {
            ObjectIdFieldName = "FID",
            IsLastPage = false,
        };
        var feature1 = new Proto.Feature { Id = 1 };
        page1.Features.Add(feature1);

        var page2 = new Proto.FeaturePage
        {
            IsLastPage = true,
        };
        var feature2 = new Proto.Feature { Id = 2 };
        page2.Features.Add(feature2);

        var mockClient = new Mock<Proto.FeatureService.FeatureServiceClient>();
        mockClient
            .Setup(c => c.QueryFeaturesStream(
                It.IsAny<Proto.QueryFeaturesRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncServerStreamingCall([page1, page2]));

        var client = new HonuaGrpcClient(mockClient.Object);
        var request = new Models.QueryFeaturesRequest
        {
            ServiceId = "test-svc",
            LayerId = 0,
        };

        var pages = new List<Models.FeaturePage>();
        await foreach (var page in client.QueryFeaturesStreamAsync(request))
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);
        Assert.Equal("FID", pages[0].ObjectIdFieldName);
        Assert.False(pages[0].IsLastPage);
        Assert.True(pages[1].IsLastPage);
        Assert.Equal(1L, pages[0].Features[0].Id);
        Assert.Equal(2L, pages[1].Features[0].Id);
    }

    [Fact]
    public async Task QueryFeaturesAsync_RpcException_WrappedInHonuaGrpcException()
    {
        var mockClient = new Mock<Proto.FeatureService.FeatureServiceClient>();
        mockClient
            .Setup(c => c.QueryFeaturesAsync(
                It.IsAny<Proto.QueryFeaturesRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.NotFound, "Layer not found")));

        var client = new HonuaGrpcClient(mockClient.Object);
        var request = new Models.QueryFeaturesRequest
        {
            ServiceId = "test-svc",
            LayerId = 999,
        };

        var ex = await Assert.ThrowsAsync<HonuaGrpcException>(() => client.QueryFeaturesAsync(request));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        Assert.Contains("Layer not found", ex.Message);
    }

    [Fact]
    public void Metadata_IncludesApiKey_WhenConfigured()
    {
        var mockClient = new Mock<Proto.FeatureService.FeatureServiceClient>();
        Metadata? capturedMetadata = null;

        var protoResponse = new Proto.QueryFeaturesResponse();
        mockClient
            .Setup(c => c.QueryFeaturesAsync(
                It.IsAny<Proto.QueryFeaturesRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<Proto.QueryFeaturesRequest, Metadata, DateTime?, CancellationToken>(
                (_, metadata, _, _) => capturedMetadata = metadata)
            .Returns(CreateAsyncUnaryCall(protoResponse));

        var metadata = new Metadata { { "x-api-key", "my-key" } };
        var client = new HonuaGrpcClient(mockClient.Object, metadata);

        _ = client.QueryFeaturesAsync(new Models.QueryFeaturesRequest { ServiceId = "svc" });

        Assert.NotNull(capturedMetadata);
        var apiKeyEntry = capturedMetadata!.FirstOrDefault(e => e.Key == "x-api-key");
        Assert.NotNull(apiKeyEntry);
        Assert.Equal("my-key", apiKeyEntry.Value);
    }

    [Fact]
    public void Metadata_IncludesBearerToken_WhenConfigured()
    {
        var mockClient = new Mock<Proto.FeatureService.FeatureServiceClient>();
        Metadata? capturedMetadata = null;

        var protoResponse = new Proto.QueryFeaturesResponse();
        mockClient
            .Setup(c => c.QueryFeaturesAsync(
                It.IsAny<Proto.QueryFeaturesRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<Proto.QueryFeaturesRequest, Metadata, DateTime?, CancellationToken>(
                (_, metadata, _, _) => capturedMetadata = metadata)
            .Returns(CreateAsyncUnaryCall(protoResponse));

        var metadata = new Metadata { { "authorization", "Bearer my-token" } };
        var client = new HonuaGrpcClient(mockClient.Object, metadata);

        _ = client.QueryFeaturesAsync(new Models.QueryFeaturesRequest { ServiceId = "svc" });

        Assert.NotNull(capturedMetadata);
        var authEntry = capturedMetadata!.FirstOrDefault(e => e.Key == "authorization");
        Assert.NotNull(authEntry);
        Assert.Equal("Bearer my-token", authEntry.Value);
    }

    [Fact]
    public void HonuaGrpcException_ContainsStatusCode()
    {
        var ex = new HonuaGrpcException(StatusCode.PermissionDenied, "Access denied");

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains("PermissionDenied", ex.Message);
        Assert.Contains("Access denied", ex.Message);
    }

    private static AsyncUnaryCall<T> CreateAsyncUnaryCall<T>(T response)
    {
        return new AsyncUnaryCall<T>(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    private static AsyncServerStreamingCall<T> CreateAsyncServerStreamingCall<T>(IEnumerable<T> responses)
    {
        var stream = new TestAsyncStreamReader<T>(responses);
        return new AsyncServerStreamingCall<T>(
            stream,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    private sealed class TestAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _enumerator;

        public TestAsyncStreamReader(IEnumerable<T> items)
        {
            _enumerator = items.GetEnumerator();
        }

        public T Current => _enumerator.Current;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            return Task.FromResult(_enumerator.MoveNext());
        }
    }
}
