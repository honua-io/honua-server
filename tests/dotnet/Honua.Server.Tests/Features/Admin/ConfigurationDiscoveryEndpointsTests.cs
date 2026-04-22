// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
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
public sealed class ConfigurationDiscoveryEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "configuration-discovery-admin-key";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ConfigurationDiscoveryEndpointsTests()
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
    [Endpoint("GET /api/v1/admin/configuration/discover")]
    public async Task DiscoverConfiguration_WithAdminAuth_ReturnsMaskedConfigurationOverview()
    {
        var response = await _client.GetAsync("/api/v1/admin/configuration/discover");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().NotContain(AdminPassword);

        using var json = JsonDocument.Parse(responseContent);
        json.RootElement.GetProperty("discoveredTypes").ValueKind.Should().Be(JsonValueKind.Array);
        json.RootElement.GetProperty("extractedMetadata").ValueKind.Should().Be(JsonValueKind.Array);
        json.RootElement.GetProperty("secretValidation").ValueKind.Should().Be(JsonValueKind.Object);
        json.RootElement.GetProperty("auditInformation").GetProperty("environment").GetString().Should().Be("Test");
        json.RootElement.GetProperty("timestamp").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("discoveryDurationMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/configuration/metadata")]
    [Endpoint("GET /api/v1/admin/configuration/auto-documentation")]
    public async Task ConfigurationDiscovery_MetadataAndDocumentationEndpoints_ReturnCollections()
    {
        var metadataResponse = await _client.GetAsync("/api/v1/admin/configuration/metadata");
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var metadataJson = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadataJson.RootElement.ValueKind.Should().Be(JsonValueKind.Array);

        var documentationResponse = await _client.GetAsync("/api/v1/admin/configuration/auto-documentation");
        documentationResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var documentationJson = JsonDocument.Parse(await documentationResponse.Content.ReadAsStringAsync());
        documentationJson.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/configuration/secrets/validate")]
    [Endpoint("GET /api/v1/admin/configuration/audit")]
    [Endpoint("GET /api/v1/admin/configuration/summary")]
    public async Task ConfigurationDiscovery_OperationalEndpoints_ReturnExpectedContracts()
    {
        var secretsResponse = await _client.GetAsync("/api/v1/admin/configuration/secrets/validate");
        secretsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var secretsJson = JsonDocument.Parse(await secretsResponse.Content.ReadAsStringAsync());
        secretsJson.RootElement.GetProperty("totalSecrets").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        secretsJson.RootElement.GetProperty("validSecrets").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        secretsJson.RootElement.GetProperty("invalidSecrets").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        secretsJson.RootElement.GetProperty("issues").ValueKind.Should().Be(JsonValueKind.Array);

        var auditResponse = await _client.GetAsync("/api/v1/admin/configuration/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var auditJson = JsonDocument.Parse(await auditResponse.Content.ReadAsStringAsync());
        auditJson.RootElement.GetProperty("environment").GetString().Should().Be("Test");
        auditJson.RootElement.GetProperty("applicationName").GetString().Should().NotBeNullOrWhiteSpace();
        auditJson.RootElement.GetProperty("configurationSources").ValueKind.Should().Be(JsonValueKind.Object);

        var summaryResponse = await _client.GetAsync("/api/v1/admin/configuration/summary");
        summaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var summaryJson = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
        summaryJson.RootElement.GetProperty("totalTypes").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        summaryJson.RootElement.GetProperty("registeredTypes").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        summaryJson.RootElement.GetProperty("environment").GetString().Should().Be("Test");
        summaryJson.RootElement.GetProperty("lastDiscovery").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
