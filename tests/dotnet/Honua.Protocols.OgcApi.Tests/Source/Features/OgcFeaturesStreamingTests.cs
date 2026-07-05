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

    /// <summary>
    /// Regression for BH4-008: streaming responses must expose an advisory
    /// <c>numberMatched</c> snapshot count (in the JSON body and
    /// <c>OGC-NumberMatched</c> header). The value is a pre-flight snapshot
    /// estimate — it is advisory per OGC API Features Part 1 §7.7, not an
    /// authoritative exact count — but it must always be a non-negative integer,
    /// never absent or negative.
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_LargeLimit_Streaming_ProvidesAdvisorySnapshotNumberMatched()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=2000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Assert.True(response.Headers.TransferEncodingChunked ?? false,
            "Response must use chunked encoding (streaming path)");

        // JSON body must contain numberMatched as an advisory snapshot count.
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("numberMatched", out var nmProp)
            .Should().BeTrue("streaming path must include an advisory numberMatched count (BH4-008)");
        nmProp.GetInt64().Should().BeGreaterThanOrEqualTo(0,
            "numberMatched snapshot estimate must be a non-negative integer");

        // HTTP header form: OGC-NumberMatched must also be set for protocol-level consumers.
        response.Headers.TryGetValues("OGC-NumberMatched", out var headerValues)
            .Should().BeTrue("streaming path must set OGC-NumberMatched response header (BH4-008)");
        var headerValue = headerValues!.First();
        long.TryParse(headerValue, out var parsedHeader).Should().BeTrue(
            "OGC-NumberMatched header must be parseable as a long integer");
        parsedHeader.Should().BeGreaterThanOrEqualTo(0,
            "OGC-NumberMatched header value must be non-negative");
    }
}
