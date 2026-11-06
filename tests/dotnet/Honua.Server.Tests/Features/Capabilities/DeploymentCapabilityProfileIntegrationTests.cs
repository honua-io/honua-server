// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Capabilities;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit.Infrastructure;
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

    [IntegrationTheory]
    [InlineData("/rest/services/Public/Parcels/FeatureServer", "serve.geoservices-featureserver", HttpStatusCode.OK)]
    [InlineData("/rest/services/Public/Parcels/FeatureServer", "serve.geoservices-mapserver", HttpStatusCode.NotFound)]
    [InlineData("/rest/services/Public/Parcels", "serve.geoservices-root", HttpStatusCode.OK)]
    [InlineData("/rest/services/Public/Parcels", "serve.stac", HttpStatusCode.NotFound)]
    [InlineData("/rest/admin/Public/Parcels.MapServer", "serve.geoservices-root", HttpStatusCode.OK)]
    [InlineData("/rest/admin/Public/Parcels.MapServer", "serve.stac", HttpStatusCode.NotFound)]
    [InlineData("/admin/services/Public/Parcels.MapServer", "serve.geoservices-root", HttpStatusCode.OK)]
    [InlineData("/admin/services/Public/Parcels.MapServer", "serve.stac", HttpStatusCode.NotFound)]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}")]
    [Endpoint("GET /rest/admin/{folderName}/{serviceName}.{serviceType}")]
    [Endpoint("GET /admin/services/{folderName}/{serviceName}.{serviceType}")]
    public async Task FolderPath_ConfiguredProfile_EnforcesItsCapability(string path, string capability, HttpStatusCode expectedStatus)
    {
        var fixture = CreateFixture([capability]);
        await fixture.InitializeAsync();
        try
        {
            var original = fixture.GetCurrentV2GraphSnapshot().Graph;
            var provider = (TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>();
            provider.SetGraph(original with
            {
                Folders = [new() { Metadata = new() { Id = "folder", Name = "Public" } }],
                Services = original.Services.Select(service => service.Metadata.Name == WebAppFixture.TestServiceId
                    ? service with { FolderId = "folder", PublicName = "Parcels" } : service).ToArray()
            }, schema: fixture.CurrentSchema);
            using var response = await fixture.Client.GetAsync(path + "?f=json");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(expectedStatus, body);
            if (expectedStatus == HttpStatusCode.OK && path.EndsWith("/FeatureServer", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(body);
                document.RootElement.GetProperty("serviceName").GetString().Should().Be("Parcels");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

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
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task ConfiguredProfile_OmittingManifestCapability_ReturnsNotFound()
    {
        var fixture = CreateFixture(["serve.stac"]);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
