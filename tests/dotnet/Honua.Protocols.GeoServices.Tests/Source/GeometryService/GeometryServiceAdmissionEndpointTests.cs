// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

[Collection("Database")]
[Protocol(TestProtocols.GeometryService)]
public sealed class GeometryServiceAdmissionEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_AnonymousCallerAtAdmissionCapacity_ReturnsRetryResponse()
    {
        var admission = new GeometryServiceAdmission(new GeometryServiceAdmission.Bound(2, 4096), 2048);
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.UseSetting("HONUA_DEV_AUTH", "false"))
            .ConfigureServices(services =>
            {
                services.RemoveAll<GeometryServiceAdmission>();
                services.AddSingleton(admission);
            });
        try
        {
            await fixture.InitializeAsync();
            await AssertRetryResponseAsync(fixture.Client, admission);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task AssertRetryResponseAsync(HttpClient client, GeometryServiceAdmission admission)
    {
        using var occupied = admission.TryEnter(1, GeometryServiceCaller.NotSignedIn);
        occupied.Should().NotBeNull();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["geometries"] = """{"geometryType":"esriGeometryPolyline","geometries":[{"paths":[[[0,0],[10,0]]]}]}""",
            ["sr"] = "3857",
            ["maxSegmentLength"] = "5",
            ["f"] = "json"
        });
        using var response = await client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/densify", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be((int)HttpStatusCode.ServiceUnavailable);
        error.GetProperty("retryable").GetBoolean().Should().BeTrue();
        error.GetProperty("retryAfterSeconds").GetInt32().Should().Be(1);
        response.Headers.RetryAfter.Should().NotBeNull();
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
        response.Headers.GetValues("Honua-Retryable").Should().ContainSingle().Which.Should().Be("true");
    }
}
