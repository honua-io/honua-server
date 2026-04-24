// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesH3Tests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/h3")]
    public async Task H3_ValidResolution_ReturnsOkOrCapabilityError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/h3?resolution=7");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);

        var content = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            root.TryGetProperty("type", out var type).Should().BeTrue();
            type.GetString().Should().Be("FeatureCollection");
        }
        else
        {
            content.Should().Contain("h3-pg");
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/h3")]
    public async Task H3_MissingResolution_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/h3");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/h3")]
    public async Task H3_InvalidCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/features/collections/99999/h3?resolution=7");

        // Collection validation runs before H3 capability check, so
        // an invalid collection always yields NotFound.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/h3")]
    public async Task H3_UnknownQueryParameter_ReturnsBadRequestWithStandardFormat()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/h3?resolution=7&bogus=1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Verify the error uses the standard problem response format (not a plain string)
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Bad Request");
    }
}
