// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesConditionalGetTests : IAsyncLifetime
{
    private const int TestLayerId = 0;
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_UncachedRepeatWithIfNoneMatch_Returns304NotModified()
    {
        var path = $"/ogc/features/collections/{TestLayerId}/items?limit=10";

        using var first = await _fixture.Client.GetAsync(path);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag.Should().NotBeNull();
        first.Headers.ETag!.IsWeak.Should().BeTrue();

        await Task.Delay(15);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag);
        using var second = await _fixture.Client.SendAsync(request);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_AfterFeatureInsert_StaleIfNoneMatchReturns200WithNewETag()
    {
        var path = $"/ogc/features/collections/{TestLayerId}/items?limit=50";

        using var first = await _fixture.Client.GetAsync(path);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var originalETag = first.Headers.ETag;
        originalETag.Should().NotBeNull();

        await _fixture.InsertFeatureAsync(TestLayerId, "ConditionalGetInvalidationProbe");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(originalETag!);
        using var afterWrite = await _fixture.Client.SendAsync(request);

        afterWrite.StatusCode.Should().Be(HttpStatusCode.OK);
        afterWrite.Headers.ETag.Should().NotBeNull();
        afterWrite.Headers.ETag!.ToString().Should().NotBe(originalETag!.ToString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithGzipAcceptEncoding_IfNoneMatchReturns304NotModified()
    {
        var path = $"/ogc/features/collections/{TestLayerId}/items?limit=10";

        using var firstReq = new HttpRequestMessage(HttpMethod.Get, path);
        firstReq.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var first = await _fixture.Client.SendAsync(firstReq);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag.Should().NotBeNull();

        await Task.Delay(15);

        using var secondReq = new HttpRequestMessage(HttpMethod.Get, path);
        secondReq.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        secondReq.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await _fixture.Client.SendAsync(secondReq);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_MaterializedCrsTransformWithIfNoneMatch_Returns304NotModified()
    {
        var path = $"/ogc/features/collections/{TestLayerId}/items?limit=10&crs=http%3A%2F%2Fwww.opengis.net%2Fdef%2Fcrs%2FEPSG%2F0%2F3857";

        using var first = await _fixture.Client.GetAsync(path);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag.Should().NotBeNull();
        first.Headers.ETag!.IsWeak.Should().BeTrue();

        await Task.Delay(15);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag);
        using var second = await _fixture.Client.SendAsync(request);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithIfNoneMatchWildcard_Returns304NotModified()
    {
        var path = $"/ogc/features/collections/{TestLayerId}/items?limit=5";

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task HeadItems_WithMatchingIfNoneMatch_Returns304NotModified()
    {
        var path = $"/ogc/features/collections/{TestLayerId}/items?limit=10";

        using var first = await _fixture.Client.GetAsync(path);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag.Should().NotBeNull();

        await Task.Delay(15);

        using var headReq = new HttpRequestMessage(HttpMethod.Head, path);
        headReq.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await _fixture.Client.SendAsync(headReq);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }
}
