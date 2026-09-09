// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// #4423: proves the NAServer anonymous contract in behaviour rather than in source text.
/// <para>
/// NAServer's six routes are <c>.AllowAnonymous()</c> <b>by design</b> (#1144, #1266): a solve is
/// a stateless computation over a fixed routing capability, not a metadata-published, per-tenant
/// feature service, so there is no published resource to gate and no denial branch to test. The
/// only assertion the repository previously made about that contract was
/// <c>EndpointAuthorizationGuardTests.cs:124</c>, which requires that the source file <i>contain
/// the words</i> <c>AllowAnonymous</c>. That is a source-text check, not a behavioural one: it
/// would pass unchanged if the routes started requiring a credential, or if they started serving
/// published feature data.
/// </para>
/// <para>
/// This test displaces the F1 development-authentication bypass — under which every existing
/// NAServer test runs as an admin — and asserts the two halves of the contract that matter for
/// the security floor: an unauthenticated solve genuinely works (so the anonymous decision is
/// real and deliberate, not an accident of the bypass), and its response carries only the
/// computed route, with none of the published service, layer or feature metadata that would mean
/// the surface had started exposing gated data.
/// </para>
/// </summary>
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
}
