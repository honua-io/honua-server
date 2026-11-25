// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.SpatialAnalytics;

[Collection("Database")]
[Protocol(TestProtocols.SpatialAnalytics)]
public sealed class SpatialAnalyticsAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("/rest/services/test/FeatureServer/0/queryBufferAggregate")]
    [InlineData("/ogc/features/collections/0/buffer-aggregate")]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    [Endpoint("POST /ogc/features/collections/{collectionId}/buffer-aggregate")]
    public Task BufferAggregate_ProtectedLayer_AnonymousRequestIsUnauthorized(string path)
        => AssertProtectedLayerPostAsync(path);

    [IntegrationTheory]
    [InlineData("/rest/services/test/FeatureServer/0/queryClusters")]
    [InlineData("/ogc/features/collections/0/clusters")]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    [Endpoint("POST /ogc/features/collections/{collectionId}/clusters")]
    public Task Clustering_ProtectedLayer_AnonymousRequestIsUnauthorized(string path)
        => AssertProtectedLayerPostAsync(path);

    [IntegrationTheory]
    [InlineData("/rest/services/test/FeatureServer/0/queryDensity")]
    [InlineData("/ogc/features/collections/0/density")]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    [Endpoint("POST /ogc/features/collections/{collectionId}/density")]
    public Task Density_ProtectedLayer_AnonymousRequestIsUnauthorized(string path)
        => AssertProtectedLayerPostAsync(path);

    [IntegrationTheory]
    [InlineData("/rest/services/test/FeatureServer/0/spatialJoin")]
    [InlineData("/ogc/features/collections/0/spatial-join")]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    [Endpoint("POST /ogc/features/collections/{collectionId}/spatial-join")]
    public Task SpatialJoin_ProtectedLayer_AnonymousRequestIsUnauthorized(string path)
        => AssertProtectedLayerPostAsync(path);

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task H3_ProtectedLayer_AnonymousRequestIsUnauthorized()
    {
        RestrictService();
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/rest/services/test/FeatureServer/0/queryH3?resolution=7&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task AssertProtectedLayerPostAsync(string path)
    {
        RestrictService();
        using var client = _fixture.CreateClient();
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private void RestrictService()
        => _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["admin"] });
}
