// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Capabilities;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Capabilities;

[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Metadata)]
public sealed class DeploymentCapabilityProfileIntegrationTests
{
    private static readonly string[] EnabledCapabilities =
    [
        "discovery.capability-manifest",
        "ops.health",
        "serve.stac",
    ];

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    [Endpoint("GET /healthz/live")]
    [Endpoint("GET /stac")]
    [Endpoint("GET /wfs")]
    public async Task ConfiguredProfile_ReportsAndEnforcesExactHttpSurface()
    {
        var fixture = CreateFixture(EnabledCapabilities);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();

        using var manifestResponse = await client.GetAsync("/api/v1/capabilities/manifest");
        manifestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
        var profile = manifest.RootElement.GetProperty("deploymentProfile");
        profile.GetProperty("configured").GetBoolean().Should().BeTrue();
        profile.GetProperty("schemaVersion").GetString().Should().Be("1.0.0");
        profile.GetProperty("enabledCapabilities").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal(EnabledCapabilities);

        using var healthResponse = await client.GetAsync("/healthz/live");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var selectedResponse = await client.GetAsync("/stac");
        selectedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var unselectedResponse = await client.GetAsync("/wfs?service=WFS&request=GetCapabilities");
            unselectedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /stac")]
    public async Task MissingProfile_PreservesFullSurfaceBehavior()
    {
        var fixture = CreateFixture(null);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/stac");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(IEnumerable<string>? capabilities)
    {
        var settings = capabilities is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>
            {
                [DeploymentCapabilityProfile.EnabledCapabilitiesKey] = string.Join(',', capabilities),
                [DeploymentCapabilityProfile.SchemaVersionKey] = DeploymentCapabilityProfile.SupportedSchemaVersion,
            };

        return new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));
        });
    }
}
