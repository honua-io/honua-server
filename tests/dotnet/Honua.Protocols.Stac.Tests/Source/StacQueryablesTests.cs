// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Integration tests for the STAC queryables endpoints (Filter Extension).
/// </summary>
[Protocol(TestProtocols.Stac)]
[Collection("Database")]
public sealed class StacQueryablesTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public StacQueryablesTests(WebAppFixture fixture) => _fixture = fixture;

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
        // stac-api-validator (Filter Ext) only accepts JSON Schema draft 2019-09 or draft-07.
        json.RootElement.GetProperty("$schema").GetString()
            .Should().Be("https://json-schema.org/draft/2019-09/schema");
        // stac-api-validator (Filter Ext) requires $id to equal the queryables URL.
        json.RootElement.GetProperty("$id").GetString()
            .Should().EndWith("/stac/queryables");
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
        json.RootElement.GetProperty("$schema").GetString()
            .Should().Be("https://json-schema.org/draft/2019-09/schema");
        json.RootElement.GetProperty("$id").GetString()
            .Should().EndWith($"/stac/collections/{collectionId}/queryables");
        json.RootElement.TryGetProperty("properties", out var properties).Should().BeTrue();
        properties.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task GetCollection_AdvertisesQueryablesRelLink()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync($"/stac/collections/{collectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("links")
            .EnumerateArray()
            .Should()
            .Contain(link =>
                link.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/queryables" &&
                link.GetProperty("href").GetString()!.EndsWith(
                    $"/stac/collections/{collectionId}/queryables", StringComparison.Ordinal));
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

/// <summary>
/// BH-S-01 regression: GET /stac/collections/{id}/queryables must enforce the same
/// resource access policy as GET /stac/collections/{id}. An unauthenticated caller
/// must not be able to read the field schema (names/types) of a private collection.
/// </summary>
[Protocol(TestProtocols.Stac)]
[Collection("Database")]
public sealed class StacQueryablesAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            // Disable the dev-auth passthrough so unauthenticated requests are truly
            // anonymous (no implicit super-user identity).
            builder.UseSetting("HONUA_DEV_AUTH", "false");
        });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // Make the test collection private — only authenticated principals may access it.
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /stac/collections/{collectionId}/queryables")]
    public async Task GetCollectionQueryables_Unauthenticated_PrivateCollection_ReturnsUnauthorized()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);

        // Unauthenticated request (no API key, no bearer token).
        var response = await _fixture.Client.GetAsync($"/stac/collections/{collectionId}/queryables");

        // The server must deny the request — 401 (unauthenticated) or 403 (forbidden).
        // Before fix BH-S-01 the handler skipped the access check and returned 200.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
