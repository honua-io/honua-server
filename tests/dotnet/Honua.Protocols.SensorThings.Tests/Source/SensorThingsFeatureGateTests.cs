// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Verifies the experimental feature gate for OGC SensorThings API (PA-096/PA-103/PA-116/PA-145).
/// When <c>Experimental:Features:SensorThings</c> is <c>false</c> (the default), no STA routes
/// are registered and all <c>/sta/v1.1/...</c> paths return 404. When the flag is <c>true</c>,
/// the routes are active and return expected responses.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
[Trait("Category", "ExperimentalGate")]
public sealed class SensorThingsFeatureGateTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public SensorThingsFeatureGateTests()
    {
        // Override the test-environment flag (appsettings.Test.json sets it to true)
        // to simulate the production default of false.
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Experimental:Features:SensorThings"] = "false",
                    });
                });
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    [Trait("Tier", "Fast")]
    public async Task StaThings_WhenFeatureDisabled_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Things");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "SensorThings routes must not be registered when Experimental:Features:SensorThings is false");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    [Trait("Tier", "Fast")]
    public async Task StaObservationsPost_WhenFeatureDisabled_Returns404()
    {
        var response = await _fixture.Client.PostAsync(
            "/sta/v1.1/Observations",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "SensorThings ingest routes must not be registered when Experimental:Features:SensorThings is false");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    [Trait("Tier", "Fast")]
    public async Task StaDatastreamsPost_WhenFeatureDisabled_Returns404()
    {
        var response = await _fixture.Client.PostAsync(
            "/sta/v1.1/Datastreams",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "SensorThings datastream creation routes must not be registered when Experimental:Features:SensorThings is false");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    [Trait("Tier", "Fast")]
    public async Task StaObservations_WhenFeatureDisabled_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Observations");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "SensorThings read routes must not be registered when Experimental:Features:SensorThings is false");
    }
}
