// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Infrastructure;

[Collection("Database")]
[Protocol(Protocols.FeatureServer, Protocols.OgcApiFeatures, Protocols.ODataV4, Protocols.Mvt)]
public sealed class LayerEnablementIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:Enabled"] = "false"
            })));

    private HttpClient _client = null!;
    private HttpClient _adminClient = null!;
    private Guid _connectionId;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient();
        _adminClient = _fixture.CreateAdminClient();

        var connectionId = await _fixture.GetTestSecureConnectionIdAsync();
        _connectionId = connectionId ?? throw new InvalidOperationException("Test secure connection was not created.");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/{layerId}/enabled")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("GET /odata/Layers({layerId})")]
    [Endpoint("GET /tiles/{layerId}/tile.json")]
    public async Task DisabledLayer_ReturnsNotFoundAcrossProtocols()
    {
        var disableRequest = new LayerEnabledRequest { Enabled = false };

        var toggleResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers/{WebAppFixture.TestLayerId}/enabled?serviceName={WebAppFixture.TestServiceId}",
            disableRequest);

        toggleResponse.Be200Ok();

        var featureServerResponse = await _client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}");
        featureServerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var ogcResponse = await _client.GetAsync($"/ogc/features/collections/{WebAppFixture.TestLayerId}");
        ogcResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var odataResponse = await _client.GetAsync($"/odata/Layers({WebAppFixture.TestLayerId})");
        odataResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var tileJsonResponse = await _client.GetAsync($"/tiles/{WebAppFixture.TestLayerId}/tile.json");
        tileJsonResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
