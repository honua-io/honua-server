// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerH3EntitlementTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Community)
        .ReplaceService<IH3CapabilityChecker>(new AlwaysAvailableH3Checker());

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Get_CommunityEdition_ReturnsPaymentRequired()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=5&f=json");

        await response.AssertGeoServicesErrorAsync(402);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_CommunityEdition_ReturnsPaymentRequired()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3",
            new { resolution = 5, f = "json" });

        await response.AssertGeoServicesErrorAsync(402);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_CommunityEdition_ReturnsPaymentRequired()
    {
        var response = await _fixture.Client.GetAsync(
            $"/tiles/{WebAppFixture.TestLayerId}/h3/1/0/0.mvt?resolution=5");

        await response.AssertGeoServicesErrorAsync(402);
    }

    private sealed class AlwaysAvailableH3Checker : IH3CapabilityChecker
    {
        public Task<bool?> IsH3AvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(true);
    }
}
