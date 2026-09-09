// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>Verifies public URL and path-prefix handling in SensorThings discovery.</summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsServiceRootPublicUrlTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Public:BaseUrl"] = "https://sensors.example.test/honua/"
            })));

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /sta/v1.1")]
    public async Task ServiceRoot_WithPublicBaseUrl_PreservesExternalOriginAndPrefix()
    {
        using var response = await _fixture.Client.GetAsync("/sta/v1.1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entitySets = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
        entitySets.Should().HaveCount(5);
        foreach (var entitySet in entitySets)
        {
            var name = entitySet.GetProperty("name").GetString();
            entitySet.GetProperty("url").GetString().Should()
                .Be($"https://sensors.example.test/honua/sta/v1.1/{name}");
        }
    }
}
