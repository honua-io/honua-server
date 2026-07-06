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
    /// <c>numberMatched</c> snapshot count in the JSON <c>FeatureCollection</c>
    /// body — the spec-compliant location (OGC 17-069r4 §7.14.4). The value is a
    /// pre-flight snapshot estimate — advisory per OGC API Features Part 1 §7.7,
    /// not an authoritative exact count — but it must always be a non-negative
    /// integer, never absent or negative.
    ///
    /// The non-standard <c>OGC-NumberMatched</c> response header was removed in
    /// #2418 (PA-122/PA-168); this test now guards that it stays absent so the
    /// count is carried only in its spec-compliant body location.
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

        // The non-standard OGC-NumberMatched header was removed in #2418 (PA-122/PA-168):
        // the count lives only in the spec-compliant JSON body location above. Guard that the
        // header is not reintroduced.
        response.Headers.TryGetValues("OGC-NumberMatched", out _)
            .Should().BeFalse("the non-standard OGC-NumberMatched header was removed in #2418 (PA-122/PA-168)");
    }
}
