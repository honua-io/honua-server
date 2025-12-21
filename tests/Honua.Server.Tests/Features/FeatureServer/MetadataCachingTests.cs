// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Xunit;

namespace Honua.Server.Tests.Features.FeatureServer;

/// <summary>
/// Integration tests for output caching on FeatureServer metadata endpoints.
/// Verifies that metadata responses are cached according to configured policies.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public sealed class MetadataCachingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        // Replace the real ILayerCatalog with test implementation
        _fixture.ReplaceService<ILayerCatalog>(new TestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(new TestFeatureStore());
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task ServiceMetadata_FirstRequest_SetsETagHeader()
    {
        // Act
        using var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");

        // Assert
        response.Should().BeSuccessful();
        response.Headers.ETag.Should().NotBeNull("First request should set ETag for caching");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task ServiceMetadata_SubsequentRequestWithETag_Returns304NotModified()
    {
        // Act - First request to get ETag
        using var firstResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");
        firstResponse.Should().BeSuccessful();
        var etag = firstResponse.Headers.ETag;
        etag.Should().NotBeNull();

        // Act - Second request with If-None-Match header
        var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/services/{TestServiceId}/FeatureServer");
        request.Headers.IfNoneMatch.Add(etag!);
        using var secondResponse = await _fixture.Client.SendAsync(request);

        // Assert
        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "Cached response should return 304 when ETag matches");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_FirstRequest_SetsETagHeader()
    {
        // Act
        using var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        // Assert
        response.Should().BeSuccessful();
        response.Headers.ETag.Should().NotBeNull("First request should set ETag for caching");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_SubsequentRequestWithETag_Returns304NotModified()
    {
        // Act - First request to get ETag
        using var firstResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");
        firstResponse.Should().BeSuccessful();
        var etag = firstResponse.Headers.ETag;
        etag.Should().NotBeNull();

        // Act - Second request with If-None-Match header
        var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");
        request.Headers.IfNoneMatch.Add(etag!);
        using var secondResponse = await _fixture.Client.SendAsync(request);

        // Assert
        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "Cached response should return 304 when ETag matches");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task ServiceMetadata_DifferentServiceIds_HaveDifferentETags()
    {
        // Act - Request metadata for two different services
        using var response1 = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");
        using var response2 = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}2/FeatureServer");

        // Assert
        response1.Should().BeSuccessful();
        response2.Should().BeSuccessful();

        var etag1 = response1.Headers.ETag;
        var etag2 = response2.Headers.ETag;

        etag1.Should().NotBeNull();
        etag2.Should().NotBeNull();
        etag1.Should().NotBe(etag2, "Different service IDs should have different ETags");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_DifferentLayers_HaveDifferentETags()
    {
        // Act - Request metadata for different layers
        using var response1 = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/0");
        using var response2 = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/1");

        // Assert
        response1.Should().BeSuccessful();
        response2.Should().BeSuccessful();

        var etag1 = response1.Headers.ETag;
        var etag2 = response2.Headers.ETag;

        etag1.Should().NotBeNull();
        etag2.Should().NotBeNull();
        etag1.Should().NotBe(etag2, "Different layer IDs should have different ETags");
    }
}
