// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ExternalServiceDiscoveryEndpointsTests : IAsyncLifetime, IDisposable
{
    private const string AdminPassword = "external-service-discovery-admin-key";

    private readonly ExternalServiceDiscoveryServiceTests.StubHttpClientFactory _httpClientFactory =
        new(ExternalServiceDiscoveryServiceTests.AllDiscoveryResponses());

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;
    private bool _disposed;

    public ExternalServiceDiscoveryEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ReplaceService<IExternalServiceDiscoveryNetworkGuard>(
                new ExternalServiceDiscoveryServiceTests.AllowingNetworkGuard())
            .ReplaceService<IHttpClientFactory>(_httpClientFactory);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClientFactory.Dispose();
        _disposed = true;
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/external-services/discover")]
    public async Task DiscoverExternalService_WithArcGisFeatureServerFixture_ReturnsCandidates()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/external-services/discover",
            JsonContent("""
            {
              "url": "https://services.example.test/arcgis/rest/services/Planning/FeatureServer"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("sourceKind").GetString().Should().Be("arcgis-feature-server");
        json.RootElement.GetProperty("serviceName").GetString().Should().Be("Planning");

        var candidate = json.RootElement.GetProperty("candidates")[0];
        candidate.GetProperty("layerId").GetInt32().Should().Be(0);
        candidate.GetProperty("name").GetString().Should().Be("Parcels");
        candidate.GetProperty("geometryType").GetString().Should().Be("esriGeometryPolygon");
        candidate.GetProperty("srid").GetInt32().Should().Be(4326);
        candidate.GetProperty("featureCount").GetInt32().Should().Be(42);
        candidate.GetProperty("fields").GetArrayLength().Should().Be(2);
        candidate.GetProperty("extent").GetProperty("xMin").GetDouble().Should().Be(-158.3);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/external-services/discover")]
    public async Task DiscoverExternalService_WithOgcApiFeaturesFixture_ReturnsCollectionCandidates()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/external-services/discover",
            JsonContent("""
            {
              "url": "https://ogc.example.test/api"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("sourceKind").GetString().Should().Be("ogc-api-features");
        json.RootElement.GetProperty("serviceType").GetString().Should().Be("OGC API Features");
        json.RootElement.GetProperty("serviceName").GetString().Should().Be("Honolulu OGC");

        var candidate = json.RootElement.GetProperty("candidates")[0];
        candidate.GetProperty("externalId").GetString().Should().Be("zoning");
        candidate.GetProperty("name").GetString().Should().Be("Zoning");
        candidate.GetProperty("layerType").GetString().Should().Be("collection");
        candidate.GetProperty("geometryType").GetString().Should().Be("feature");
        candidate.GetProperty("srid").GetInt32().Should().Be(4326);
        candidate.GetProperty("featureCount").GetInt32().Should().Be(7);
        candidate.GetProperty("extent").GetProperty("xMin").GetDouble().Should().Be(-158.3);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/external-services/discover")]
    public async Task DiscoverExternalService_WithMissingUrl_ReturnsBadRequest()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/external-services/discover",
            JsonContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Url is required");
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");
}
