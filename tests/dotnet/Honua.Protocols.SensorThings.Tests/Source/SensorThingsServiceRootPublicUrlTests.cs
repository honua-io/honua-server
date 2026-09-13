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
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Thing")]
    [Endpoint("GET /sta/v1.1/Observations({id})/Datastream")]
    public async Task Navigation_WithPublicBaseUrl_PreservesExternalOriginAndPrefix()
    {
        foreach (var path in new[] { "/sta/v1.1/Datastreams(1)/Thing", "/sta/v1.1/Observations(1)/Datastream" })
        {
            using var response = await _fixture.Client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(1);
            var links = document.RootElement.EnumerateObject()
                .Where(property => property.Name.EndsWith("@iot.navigationLink", StringComparison.Ordinal)
                    || property.Name == "@iot.selfLink").ToArray();
            links.Should().NotBeEmpty();
            foreach (var link in links)
            {
                link.Value.GetString().Should().StartWith("https://sensors.example.test/honua/sta/v1.1/");
            }
        }
    }

}
