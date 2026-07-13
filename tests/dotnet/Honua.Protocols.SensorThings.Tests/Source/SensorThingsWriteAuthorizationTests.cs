// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Verifies SensorThings write-surface authorization defaults and the explicit
/// anonymous-write escape hatch.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsWriteAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    [Trait("Tier", "Fast")]
    public async Task PostObservation_AnonymousWhenAnonymousWritesDisabled_ReturnsUnauthorizedBeforeBodyValidation()
    {
        using var content = new StringContent("{", Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/sta/v1.1/Observations", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    [InterfaceOperation(TestProtocols.SensorThings, "sensor.ingest")]
    [Trait("Tier", "Fast")]
    public async Task PostObservation_WithAdminApiKeyWhenAnonymousWritesDisabled_CreatesObservation()
    {
        using var adminClient = _fixture.CreateAdminClient();
        using var payload = new StringContent(
            """{ "result": 8.25, "Datastream": { "@iot.id": 1 } }""",
            Encoding.UTF8,
            "application/json");

        var response = await adminClient.PostAsync("/sta/v1.1/Observations", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}

/// <summary>
/// Verifies the intentionally loud anonymous-write opt-in for SensorThings ingest.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsAnonymousWriteOptInTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SensorThings:AllowAnonymousWritesDangerously"] = "true",
                });
            });
        });

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    [InterfaceOperation(TestProtocols.SensorThings, "sensor.ingest")]
    [Trait("Tier", "Fast")]
    public async Task PostObservation_WhenAnonymousWritesDangerouslyEnabled_AllowsAnonymousWrite()
    {
        using var payload = new StringContent(
            """{ "result": 9.5, "Datastream": { "@iot.id": 1 } }""",
            Encoding.UTF8,
            "application/json");

        var response = await _fixture.Client.PostAsync("/sta/v1.1/Observations", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
