// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

// A seeded public layer requested through the real pipeline with production authentication: the path a CDN sees.
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class PublicLayerSharedCacheTests : IAsyncLifetime
{
    private const string VectorTile = "/tiles/0/1/0/0.mvt";
    private const string VectorTileServerTile = "/rest/services/test/VectorTileServer/tile/1/0/0.pbf";
    private const string FeatureQuery = "/rest/services/test/FeatureServer/0/query?f=json&where=1%3D1&outFields=*";

    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        AllowAnonymous(true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Operation(Operations.GetTile, Operations.Query)]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData(VectorTile)]
    [InlineData(VectorTileServerTile)]
    [InlineData(FeatureQuery)]
    public async Task PublicLayer_AnonymousRequest_IsSharedWithACdnLifetime(string address)
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync(address);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.SharedMaxAge.Should().NotBeNull();
        response.Headers.CacheControl.NoStore.Should().BeFalse();
        response.Headers.Vary.Should().Contain("Origin");
    }

    [IntegrationTheory]
    [Operation(Operations.GetTile, Operations.Query)]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData(VectorTile, "X-API-Key", WebAppFixture.SharedAdminPassword)]
    [InlineData(VectorTileServerTile, "X-API-Key", WebAppFixture.SharedAdminPassword)]
    [InlineData(FeatureQuery, "X-API-Key", WebAppFixture.SharedAdminPassword)]
    [InlineData(VectorTile, "Authorization", "Bearer made-up")]
    [InlineData(VectorTileServerTile, "X-Honua-Tenant", "made-up")]
    [InlineData(FeatureQuery, "X-API-Key", "made-up")]
    public async Task PublicLayer_RequestWithAnyCredential_IsNeverShared(string address, string header, string value)
    {
        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation(header, value);

        using var response = await client.SendAsync(request);

        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.CacheControl.Private.Should().BeTrue();
        response.Headers.CacheControl.Public.Should().BeFalse();
    }

    [IntegrationTheory]
    [Operation(Operations.GetTile, Operations.Query)]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData(VectorTile)]
    [InlineData(VectorTileServerTile)]
    [InlineData(FeatureQuery)]
    public async Task SecuredLayer_AnonymousRequest_IsNeverShared(string address)
    {
        using var client = _fixture.CreateClient();
        AllowAnonymous(false);

        using var afterSecuring = await client.GetAsync(address);

        afterSecuring.Headers.CacheControl.Should().NotBeNull();
        afterSecuring.Headers.CacheControl!.NoStore.Should().BeTrue();
        afterSecuring.Headers.CacheControl.Public.Should().BeFalse();
    }

    private void AllowAnonymous(bool allowed)
    {
        var policy = new AccessPolicy { AllowAnonymous = allowed };
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, accessPolicy: policy);
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: policy);
    }
}
