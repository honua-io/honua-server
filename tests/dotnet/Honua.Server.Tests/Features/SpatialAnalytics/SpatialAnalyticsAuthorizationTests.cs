// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.SpatialAnalytics;

[Collection("Database")]
[Protocol(TestProtocols.SpatialAnalytics)]
public sealed class SpatialAnalyticsAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .WithTestAuthorization();

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
        RestrictLayer();
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/rest/services/test/FeatureServer/0/queryH3?resolution=7&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await response.AssertGeoServicesErrorAsync(499);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task H3_ReaderFromAnotherLayer_IsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(() => new RbacTestLayerCatalog(
            alphaLayerMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["alpha-reader"]),
            betaLayerMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["beta-reader"])));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await client.GetAsync(
            "/rest/services/beta/FeatureServer/1/queryH3?resolution=7&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await response.AssertGeoServicesErrorAsync(403);
    }

    private async Task AssertProtectedLayerPostAsync(string path)
    {
        RestrictLayer();
        using var client = _fixture.CreateClient();
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, body);

        if (path.StartsWith("/rest/", StringComparison.Ordinal))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await response.AssertGeoServicesErrorAsync(499);
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private void RestrictLayer()
        => _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["admin"] });
}
