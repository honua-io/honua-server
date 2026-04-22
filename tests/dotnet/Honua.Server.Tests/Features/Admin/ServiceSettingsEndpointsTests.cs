// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ServiceSettingsEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "service-settings-admin-key";
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ServiceSettingsEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task ListServices_WithAdminAuth_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/v1/admin/services");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task ListServices_ResponseIncludesLayerCountAndEnabledProtocols()
    {
        var response = await _client.GetAsync("/api/v1/admin/services");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        data.GetArrayLength().Should().BeGreaterThan(0);
        var first = data[0];
        first.TryGetProperty("layerCount", out _).Should().BeTrue();
        first.TryGetProperty("enabledProtocols", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_WithServiceName_ReturnsSettingsOrNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_ResponseIncludesAccessPolicyAndTimeInfo()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        // accessPolicy and timeInfo should be present as properties (may be null)
        data.TryGetProperty("accessPolicy", out _).Should().BeTrue("response should include accessPolicy field");
        data.TryGetProperty("timeInfo", out _).Should().BeTrue("response should include timeInfo field");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_WithDefaultMetadata_IncludesGrpcInEnabledAndAvailableProtocols()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");

        var enabledProtocols = data.GetProperty("enabledProtocols")
            .EnumerateArray()
            .Select(protocol => protocol.GetString())
            .OfType<string>()
            .ToArray();

        var availableProtocols = data.GetProperty("availableProtocols")
            .EnumerateArray()
            .Select(protocol => protocol.GetString())
            .OfType<string>()
            .ToArray();

        enabledProtocols.Should().Contain("Grpc");
        availableProtocols.Should().Contain("Grpc");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    public async Task UpdateProtocols_WithValidPayload_ReturnsUpdatedOrNotFound()
    {
        var body = """
            {
              "enabledProtocols": ["FeatureServer", "MapServer", "OgcFeatures", "OData"]
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    public async Task UpdateProtocols_WithEmptyArray_Returns400()
    {
        var body = """
            {
              "enabledProtocols": []
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("At least one protocol");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/mapserver")]
    public async Task UpdateMapServerSettings_WithValidPayload_ReturnsUpdatedOrNotFound()
    {
        var payload = JsonSerializer.Serialize(new
        {
            maxImageWidth = 4096,
            maxImageHeight = 4096,
            defaultFormat = "png",
            defaultTransparent = true
        });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/mapserver", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_WithValidPayload_ReturnsUpdatedOrNotFound()
    {
        var body = """
            {
              "allowAnonymous": true
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/access-policy", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/timeinfo")]
    public async Task UpdateTimeInfo_WithValidPayload_ReturnsUpdatedOrNotFound()
    {
        var body = """
            {
              "startTimeField": "created_at"
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/timeinfo", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_WithValidPayload_ReturnsUpdatedOrNotFound()
    {
        var body = """
            {
              "accessPolicy": {
                "allowAnonymous": true
              }
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/v1/admin/services/test/layers/1/metadata", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer")]
    public async Task UpdateProtocols_DisableFeatureServer_BlocksFeatureServerServiceMetadata()
    {
        var body = """
            {
              "enabledProtocols": ["MapServer", "OgcFeatures", "OData", "Grpc"]
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var featureServerResponse = await _fixture.Client.GetAsync("/rest/services/test/FeatureServer?f=json");
        featureServerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}")]
    public async Task UpdateProtocols_DisableFeatureServer_BlocksFeatureServerLayerMetadata()
    {
        var body = """
            {
              "enabledProtocols": ["MapServer", "OgcFeatures", "OData", "Grpc"]
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var layerMetadataResponse = await _fixture.Client.GetAsync("/rest/services/test/FeatureServer/1?f=json");
        layerMetadataResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task UpdateProtocols_DisableFeatureServer_BlocksLayerTiles()
    {
        var body = """
            {
              "enabledProtocols": ["MapServer", "OgcFeatures", "OData", "Grpc"]
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tileResponse = await _client.GetAsync("/tiles/1/0/0/0.mvt");
        tileResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /odata/Layers({layerId})")]
    public async Task UpdateProtocols_DisableOData_BlocksODataLayerMetadata()
    {
        var body = """
            {
              "enabledProtocols": ["FeatureServer", "MapServer", "OgcFeatures", "Grpc"]
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var odataResponse = await _client.GetAsync("/odata/Layers(1)");
        odataResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/protocols")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task UpdateProtocols_DisableOgcFeatures_BlocksOgcCollectionMetadata()
    {
        var body = """
            {
              "enabledProtocols": ["FeatureServer", "MapServer", "OData", "Grpc"]
            }
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var updateResponse = await _client.PutAsync("/api/v1/admin/services/test/protocols", content);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var ogcResponse = await _client.GetAsync("/ogc/features/collections/1");
        ogcResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
