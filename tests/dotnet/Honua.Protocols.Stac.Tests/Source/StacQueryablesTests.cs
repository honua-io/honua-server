// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Integration tests for the STAC queryables endpoints (Filter Extension).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacQueryablesTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /stac/queryables")]
    public async Task GetCatalogQueryables_ReturnsJsonSchemaDocument()
    {
        var response = await _fixture.Client.GetAsync("/stac/queryables");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/schema+json");

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("object");
        json.RootElement.TryGetProperty("$schema", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("properties", out var properties).Should().BeTrue();
        properties.ValueKind.Should().Be(JsonValueKind.Object);
        properties.EnumerateObject().Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /stac/collections/{collectionId}/queryables")]
    public async Task GetCollectionQueryables_ById_ReturnsJsonSchemaDocument()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync($"/stac/collections/{collectionId}/queryables");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/schema+json");

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("object");
        json.RootElement.TryGetProperty("$schema", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("properties", out var properties).Should().BeTrue();
        properties.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /stac/collections/{collectionId}/queryables")]
    public async Task GetCollectionQueryables_NotFound_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/stac/collections/99999/queryables");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
