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
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_WithServiceName_ReturnsSettingsOrNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
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
}
