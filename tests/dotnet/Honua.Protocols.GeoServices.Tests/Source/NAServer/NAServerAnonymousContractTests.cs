// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.NAServer)]
public sealed class NAServerAnonymousContractTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        })
        .ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider, TestRoutingProvider>();
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_WithoutAnyCredentialAndDevAuthOff_SolvesAndDisclosesNoPublishedData()
    {
        var stops = Uri.EscapeDataString("-157.858333,21.306944;-157.862,21.31");

        // A dedicated client with no credential at all: the fixture's shared client carries an
        // admin X-API-Key, which would make an anonymous outcome unobservable.
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync(
            $"/rest/services/Routing/NAServer/Route/solve?f=json&stops={stops}&returnRoutes=true&returnDirections=true");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "NAServer solves are anonymous by design (#1144, #1266) and must stay so with the "
            + "development authentication bypass off; body: {0}",
            body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // The computed route is returned in full: the anonymous contract is a real capability,
        // not an empty 200.
        root.GetProperty("routes").GetProperty("features").GetArrayLength().Should().Be(1);
        root.GetProperty("directions").GetArrayLength().Should().BeGreaterThan(0);

        // ...and nothing beyond it. A solve response must never carry published-service
        // metadata or feature rows; if NAServer ever starts resolving a published resource,
        // this is the assertion that fails and forces an authorization decision to be made.
        root.TryGetProperty("layers", out _).Should().BeFalse();
        root.TryGetProperty("services", out _).Should().BeFalse();
        body.Should().NotContain("\"serviceItemId\"");
        body.Should().NotContain("\"objectIdFieldName\"");
    }

    [IntegrationTest]
    [Operation(Operations.ServiceArea)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea")]
    public async Task ServiceArea_GetWithoutCredentialsAndDevAuthOff_ReturnsPolygons()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync(
            "/rest/services/Routing/NAServer/ServiceArea/solveServiceArea" +
            "?f=json&facilities=-157.858333%2C21.306944&defaultBreaks=5%2C10");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("saPolygons").GetProperty("features").GetArrayLength().Should().Be(2);
        root.TryGetProperty("layers", out _).Should().BeFalse();
        root.TryGetProperty("services", out _).Should().BeFalse();
    }
}
