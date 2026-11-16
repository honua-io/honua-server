// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.RateLimiting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Middleware;

[Protocol(TestProtocols.TestQuality)]
public sealed class AnonymousPressureAdmissionTests
{
    [IntegrationTheory]
    [InlineData("GET", "/rest/services/test/FeatureServer/0/query")]
    [InlineData("HEAD", "/rest/services/test/FeatureServer/0/query")]
    [InlineData("GET", "/rest/services/test/FeatureServer/0/queryBins")]
    [InlineData("GET", "/rest/services/test/FeatureServer/0/queryH3")]
    [InlineData("GET", "/stac/search")]
    [InlineData("POST", "/stac/search")]
    [InlineData("GET", "/ogc/maps/map")]
    [InlineData("GET", "/ogc/maps/collections/test/styles/default/map")]
    [InlineData("POST", "/rest/services/test/FeatureServer/0/applyEdits")]
    [InlineData("GET", "/odata/Features?$filter=id%20gt%200")]
    [InlineData("GET", "/ogc/features/collections/test/items")]
    [InlineData("GET", "/odata/Layers(1)/Features?$filter=id%20gt%200")]
    [InlineData("GET", "/odata/Layers(1)/Features/$count")]
    [InlineData("GET", "/stac/collections/test/items")]
    [InlineData("GET", "/stac/collections/test/items/item-1")]
    [InlineData("GET", "/ogc/records/collections/test/items")]
    [InlineData("GET", "/ogc/records/collections/test/items/record-1")]
    [InlineData("GET", "/ogc/services/test/wmts?SERVICE=WMTS&REQUEST=GetFeatureInfo")]
    [InlineData("POST", "/rest/services/test/FeatureServer/createReplica")]
    [InlineData("POST", "/rest/services/test/FeatureServer/extractChanges")]
    [InlineData("POST", "/rest/services/test/FeatureServer/synchronizeReplica")]
    [InlineData("POST", "/rest/services/test/FeatureServer/0/1/addAttachment")]
    [InlineData("GET", "/sta/v1.1/Observations?$expand=Datastream")]
    [InlineData("GET", "/sta/v1.1/Datastreams(1)/Observations")]
    [InlineData("POST", "/sta/v1.1/Observations")]
    [InlineData("POST", "/rest/services/Utilities/Geometry/GeometryServer/densify")]
    [InlineData("GET", "/rest/services/Utilities/Geometry/GeometryServer/convexHull")]
    [InlineData("POST", "/rest/services/Utilities/Geometry/GeometryServer/relation")]
    [InlineData("POST", "/rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString")]
    [InlineData("POST", "/rest/services/Utilities/Geometry/GeometryServer/buffer")]
    [Operation(Operations.Infrastructure)]
    public async Task InvokeAsync_AnonymousWorkUnderPressure_ReturnsRetryWithoutReachingDatabase(string method, string path)
    {
        var gate = PressuredGate();
        await using var app = await StartAsync(gate);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await app.GetTestClient().SendAsync(request);

        response.Headers.Contains("X-Database-Reached").Should().BeFalse();
        response.Headers.GetValues("Honua-Retryable").Should().ContainSingle().Which.Should().Be("true");
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    [IntegrationTheory]
    [InlineData("/healthz/live")]
    [InlineData("/ogc/maps")]
    [InlineData("/ogc/maps/conformance")]
    [InlineData("/ogc/maps/openapi.json")]
    [InlineData("/ogc/maps/collections/test")]
    [InlineData("/ogc/maps/collections/test/map/tiles")]
    [InlineData("/odata/Layers")]
    [InlineData("/ogc/records/collections")]
    [InlineData("/stac/collections/test")]
    [InlineData("/sta/v1.1")]
    [InlineData("/rest/services/test/FeatureServer/0/1/attachments")]
    [InlineData("/rest/services/test/FeatureServer")]
    [InlineData("/rest/services/Utilities/Geometry/GeometryServer")]
    [InlineData("/api/v1/admin/auth/providers/entra/authorize-url")]
    [Operation(Operations.Infrastructure)]
    public async Task InvokeAsync_InexpensiveRouteUnderPressure_RemainsAvailable(string path)
    {
        await using var app = await StartAsync(PressuredGate());
        using var response = await app.GetTestClient().GetAsync(path);

        response.Headers.Contains("X-Database-Reached").Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Infrastructure)]
    public async Task InvokeAsync_WfsBodyOverridesCapabilitiesQuery_ShedsAnonymousWork()
    {
        await using var app = await StartAsync(PressuredGate());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/wfs?request=GetCapabilities")
        {
            Content = new StringContent("<wfs:GetFeature xmlns:wfs=\"http://www.opengis.net/wfs/2.0\"/>",
                System.Text.Encoding.UTF8, "application/xml")
        };
        using var response = await app.GetTestClient().SendAsync(request);

        response.Headers.Contains("X-Database-Reached").Should().BeFalse();
        response.Headers.Contains("Honua-Retryable").Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Infrastructure)]
    public async Task InvokeAsync_SignedInWorkUnderPressure_RemainsAvailable()
    {
        await using var app = await StartAsync(PressuredGate());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/test/FeatureServer/0/query");
        request.Headers.Add("Test-Authenticated", "yes");
        using var response = await app.GetTestClient().SendAsync(request);

        response.Headers.Contains("X-Database-Reached").Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Infrastructure)]
    public async Task InvokeAsync_PressureClears_AnonymousWorkRecoversDespiteEarlierTimeouts()
    {
        var gate = PressuredGate();
        await using var app = await StartAsync(gate);
        var client = app.GetTestClient();
        using var denied = await client.GetAsync("/rest/services/test/FeatureServer/0/query");
        denied.Headers.Contains("X-Database-Reached").Should().BeFalse();
        gate.GetPressure().Returns(new DatabaseAdmissionPressure(1, 10, 0, 0, 20));

        using var recovered = await client.GetAsync("/rest/services/test/FeatureServer/0/query");
        recovered.Headers.Contains("X-Database-Reached").Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Infrastructure)]
    public async Task InvokeAsync_QueuedWorkWithLowUtilization_ShedsAnonymousWork()
    {
        var gate = PressuredGate();
        gate.GetPressure().Returns(new DatabaseAdmissionPressure(1, 10, 1, 0, 0));
        await using var app = await StartAsync(gate);
        using var response = await app.GetTestClient().GetAsync("/rest/services/test/FeatureServer/0/query");

        response.Headers.Contains("X-Database-Reached").Should().BeFalse();
    }

    private static IRuntimeTunableAdmissionGate PressuredGate()
    {
        var gate = Substitute.For<IRuntimeTunableAdmissionGate>();
        gate.GetPressure().Returns(new DatabaseAdmissionPressure(8, 10, 0, 0, 0));
        return gate;
    }

    private static async Task<WebApplication> StartAsync(IRuntimeTunableAdmissionGate gate)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(gate);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("Test-Authenticated"))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
            }
            await next(context);
        });
        app.UseMiddleware<AnonymousPressureAdmissionMiddleware>();
        app.Run(context =>
        {
            context.Response.Headers["X-Database-Reached"] = "true";
            return context.Response.WriteAsync("available");
        });
        await app.StartAsync();
        return app;
    }
}
