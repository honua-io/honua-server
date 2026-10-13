// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>Provider-level routing discovery without a feature-service catalog publication.</summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GPServer)]
public sealed class RoutingUtilityServiceTests : IClassFixture<NAServerEndpointTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public RoutingUtilityServiceTests(NAServerEndpointTestsFixture wrapper)
    {
        _fixture = wrapper.App;
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task RoutingRoot_WithoutCatalogPublication_ListsOnlySupportedSynchronousTools()
    {
        using var response = await _fixture.Client.GetAsync("/rest/services/Routing/GPServer?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();
        root.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeSynchronous");
        root.GetProperty("capabilities").GetString().Should().BeEmpty();
        root.GetProperty("resultMapServerName").GetString().Should().BeEmpty();
        var tasks = root.GetProperty("tasks").EnumerateArray().Select(task => task.GetString()).ToArray();
        tasks.Should().Equal("FindRoutes", "GenerateServiceAreas", "GetTravelModes", "GetToolInfo");
        foreach (var task in tasks)
        {
            using var taskResponse = await _fixture.Client.GetAsync($"/rest/services/Routing/GPServer/{task}?f=json");
            using var taskDocument = JsonDocument.Parse(await taskResponse.Content.ReadAsStringAsync());
            taskDocument.RootElement.GetProperty("name").GetString().Should().Be(task);
            taskDocument.RootElement.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeSynchronous");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer")]
    public async Task RoutingRoot_PostAndAlias_ReturnsTheSameDescriptor()
    {
        using var response = await _fixture.Client.PostAsync("/arcgis/rest/services/routing/GPServer",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" }));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("tasks").EnumerateArray().Select(task => task.GetString())
            .Should().Equal("FindRoutes", "GenerateServiceAreas", "GetTravelModes", "GetToolInfo");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task OrdinaryUnknownService_DoesNotBecomeARoutingService()
    {
        await AssertErrorAsync(_fixture.Client, "/rest/services/not-a-published-service/GPServer?f=json", 404);
        await AssertErrorAsync(_fixture.Client, "/rest/services/Routing/GPServer/geometry.buffer?f=json", 404);
        await AssertErrorAsync(_fixture.Client, "/rest/services/Routing/GPServer?f=html", 400);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task RoutingRoot_RouteUnsupported_OmitsToolAndRejectsItsMetadataAndExecution()
    {
        var fixture = await CreateFixtureAsync(new RoutingProviderCapabilities(SupportsRoute: false, SupportsServiceArea: true));
        try
        {
            using var response = await fixture.Client.GetAsync("/rest/services/Routing/GPServer?f=json");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("tasks").EnumerateArray().Select(task => task.GetString())
                .Should().Equal("GenerateServiceAreas", "GetTravelModes", "GetToolInfo");
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer/FindRoutes?f=json", 404);
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer/FindRoutes/execute?f=json", 404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task RoutingRoot_NoSynchronousCapabilities_DoesNotAdvertiseAnAvailableService()
    {
        var fixture = await CreateFixtureAsync(new RoutingProviderCapabilities(SupportsRoute: false, SupportsServiceArea: false));
        try
        {
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer?f=json", 404);
            foreach (var task in new[] { "FindRoutes", "GenerateServiceAreas", "GetTravelModes", "GetToolInfo" })
            {
                await AssertErrorAsync(fixture.Client, $"/rest/services/Routing/GPServer/{task}?f=json", 404);
                await AssertErrorAsync(fixture.Client, $"/rest/services/Routing/GPServer/{task}/execute?f=json", 404);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ExistingPrivateRoutingService_RootTaskAndExecuteKeepItsAccessPolicy()
    {
        var fixture = await CreateCatalogCollisionFixtureAsync(gpServerEnabled: true);
        try
        {
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer?f=json", 401);
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer/GetTravelModes?f=json", 401);
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer/FindRoutes/execute?f=json", 401);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ExistingProtocolDisabledRoutingService_RootTaskAndExecuteDoNotFallBackToProvider()
    {
        var fixture = await CreateCatalogCollisionFixtureAsync(gpServerEnabled: false);
        try
        {
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer?f=json", 404);
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer/GetTravelModes?f=json", 404);
            await AssertErrorAsync(fixture.Client, "/rest/services/Routing/GPServer/FindRoutes/execute?f=json", 404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<WebAppFixture> CreateCatalogCollisionFixtureAsync(bool gpServerEnabled)
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-private-routing", Name = "Routing" },
                    Protocols = gpServerEnabled ? ["GPServer"] : ["FeatureServer"],
                    AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["routing-reader"] }
                }
            ]
        };
        var provider = Substitute.For<IMetadataV2GraphProvider>();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<MetadataV2GraphSnapshot>(new MetadataV2GraphSnapshot(graph, "routing-collision", DateTimeOffset.UtcNow)));
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.AddSingleton(provider);
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider, TestRoutingProvider>();
        });
        await fixture.InitializeAsync();
        return fixture;
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(RoutingProviderCapabilities capabilities)
    {
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider>(_ => new TestRoutingProvider(capabilities));
        });
        await fixture.InitializeAsync();
        return fixture;
    }

    private static async Task AssertErrorAsync(HttpClient client, string path, int expectedCode)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(expectedCode);
    }
}
