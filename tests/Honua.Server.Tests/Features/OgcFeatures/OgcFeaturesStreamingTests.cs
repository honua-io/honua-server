// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesStreamingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _fixture.EnsureLargeTestDatasetAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_LargeLimit_UsesStreamingResponse()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=2000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Assert.True(response.Headers.TransferEncodingChunked ?? false,
            "Expected chunked transfer encoding for streaming responses");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        document.RootElement.GetProperty("features").EnumerateArray().Should().NotBeEmpty();
        document.RootElement.TryGetProperty("numberMatched", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("numberReturned", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items?f=gml")]
    public async Task GetItems_LargeLimit_Gml_UsesStreamingResponse()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=2000&f=gml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Assert.True(response.Headers.TransferEncodingChunked ?? false,
            "Expected chunked transfer encoding for streaming GML responses");

        response.Content.Headers.ContentType?.MediaType.Should().Contain("application/gml+xml");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("<wfs:FeatureCollection");
    }
}
