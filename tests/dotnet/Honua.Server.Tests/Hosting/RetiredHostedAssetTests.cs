// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Honua.Server.Tests.Hosting;

[Protocol(TestProtocols.Infrastructure)]
public sealed class RetiredHostedAssetTests
{
    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task FilterHostedBlazorStaticAssetEndpoints_StaleAssetRoutes_PreservesProductRoutes()
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        app.MapGet("admin/index.html", () => Results.Ok());
        app.MapGet("samples/stac-ops/index.html", () => Results.Ok());
        app.MapGet("/stac", () => Results.Ok());

        HostedBlazorAssetHelpers.FilterHostedBlazorStaticAssetEndpoints(app);

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText);
        routes.Should().Equal("/stac");
    }
}
