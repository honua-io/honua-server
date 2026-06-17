// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesStreamingTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        await App.EnsureLargeTestDatasetAsync();
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
[Collection("Database")]
public sealed class OgcFeaturesStreamingTests : IClassFixture<OgcFeaturesStreamingTestsFixture>
{
    private readonly WebAppFixture _fixture;
    private const int TestLayerId = 0;

    public OgcFeaturesStreamingTests(OgcFeaturesStreamingTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

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
