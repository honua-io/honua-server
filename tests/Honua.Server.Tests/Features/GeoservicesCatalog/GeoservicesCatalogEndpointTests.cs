// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.GeoservicesCatalog;

[Collection("Database")]
[Protocol(Protocols.GeoservicesCatalog)]
public sealed class GeoservicesCatalogEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_DefaultFormat_ReturnsCatalogPayload()
    {
        var response = await _fixture.Client.GetAsync("/rest/services");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.TryGetProperty("currentVersion", out _).Should().BeTrue();
        payload.RootElement.TryGetProperty("folders", out var folders).Should().BeTrue();
        folders.ValueKind.Should().Be(JsonValueKind.Array);
        payload.RootElement.TryGetProperty("services", out var services).Should().BeTrue();
        services.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_PjsonFormat_ReturnsCatalogPayload()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=pjson");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_InvalidFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=xml");

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/info")]
    public async Task GetRestInfo_DefaultFormat_ReturnsRootInfo()
    {
        var response = await _fixture.Client.GetAsync("/rest/info");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.TryGetProperty("currentVersion", out _).Should().BeTrue();
        payload.RootElement.TryGetProperty("fullVersion", out _).Should().BeTrue();
        payload.RootElement.TryGetProperty("authInfo", out var authInfo).Should().BeTrue();
        authInfo.TryGetProperty("isTokenBasedSecurity", out _).Should().BeTrue();
    }
}
