// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.NAServer)]
public sealed class PortalHelperServicesTests : IClassFixture<NAServerEndpointTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public PortalHelperServicesTests(NAServerEndpointTestsFixture wrapper) => _fixture = wrapper.App;

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_SupportedHelpers_ResolveToMatchingAnalysisLayers()
    {
        using var response = await _fixture.Client.GetAsync("/sharing/rest/portals/self?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var helpers = document.RootElement.GetProperty("helperServices");
        helpers.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "route", "serviceArea", "closestFacility", "odCostMatrix");

        foreach (var (property, layerName) in new[]
                 {
                     ("route", "Route"), ("serviceArea", "ServiceArea"),
                     ("closestFacility", "ClosestFacility"), ("odCostMatrix", "ODCostMatrix"),
                 })
        {
            var url = helpers.GetProperty(property).GetProperty("url").GetString();
            url.Should().EndWith($"/rest/services/Routing/NAServer/{layerName}");
            using var layerResponse = await _fixture.Client.GetAsync(url + "?f=json");
            layerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var layerDocument = JsonDocument.Parse(await layerResponse.Content.ReadAsStringAsync());
            layerDocument.RootElement.TryGetProperty("error", out _).Should().BeFalse();
            layerDocument.RootElement.GetProperty("layerName").GetString().Should().Be(layerName);
        }
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_RestrictedProvider_OmitsUnavailableHelpers(bool supportsRoute)
    {
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider>(_ => new TestRoutingProvider(
                new RoutingProviderCapabilities(SupportsRoute: supportsRoute, SupportsServiceArea: false)));
        });
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.Client.GetAsync("/sharing/rest/portals/self?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var helpers = document.RootElement.GetProperty("helperServices");
            helpers.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                supportsRoute ? ["route"] : Array.Empty<string>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_UnavailableRoutingProvider_PreservesPortalIdentityWithoutHelpers(bool factoryFails)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RoutingProviderCapabilities>(new InvalidOperationException("Dataset unavailable")));
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider>(_ => factoryFails
                ? throw new InvalidOperationException("Provider unavailable")
                : provider);
        });
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.Client.GetAsync("/sharing/rest/portals/self?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("isPortal").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("helperServices").EnumerateObject().Should().BeEmpty();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_AnonymousWithPublicBaseUrl_PreservesCanonicalPrefix()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("Public:BaseUrl", "https://routing.example.test/arcgis");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRoutingProvider>();
                services.AddScoped<IRoutingProvider, TestRoutingProvider>();
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/sharing/rest/portals/self?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("user").ValueKind.Should().Be(JsonValueKind.Null);
            document.RootElement.GetProperty("helperServices").GetProperty("route")
                .GetProperty("url").GetString().Should().Be(
                    "https://routing.example.test/arcgis/rest/services/Routing/NAServer/Route");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
