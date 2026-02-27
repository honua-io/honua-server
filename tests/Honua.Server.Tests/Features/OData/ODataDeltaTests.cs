// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Integration tests for OData $deltatoken change tracking support.
/// Verifies that delta links are emitted on final pages and that
/// subsequent requests with $deltatoken are accepted and return results.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataDeltaTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithoutDeltaToken_ReturnsDeltaLink()
    {
        // Request all features (single page, no nextLink)
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // When all results fit in one page, a deltaLink should be present
        document.RootElement.TryGetProperty("@odata.nextLink", out _).Should().BeFalse(
            "all results should fit in one page");

        document.RootElement.TryGetProperty("@odata.deltaLink", out var deltaLinkElement).Should().BeTrue(
            "a delta link should be emitted when there are no more pages");

        var deltaLink = deltaLinkElement.GetString();
        deltaLink.Should().NotBeNullOrEmpty();
        deltaLink.Should().Contain("$deltatoken=");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithDeltaToken_ReturnsResults()
    {
        // First, get a deltaLink
        var firstResponse = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=100");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstContent = await firstResponse.Content.ReadAsStringAsync();
        using var firstDocument = JsonDocument.Parse(firstContent);

        var deltaLink = firstDocument.RootElement.GetProperty("@odata.deltaLink").GetString();
        var deltaUri = new Uri(deltaLink!);

        // Follow the delta link
        var secondResponse = await _fixture.Client.GetAsync(deltaUri.PathAndQuery);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondContent = await secondResponse.Content.ReadAsStringAsync();
        using var secondDocument = JsonDocument.Parse(secondContent);

        // Since no changes were made between requests, the delta response
        // should still be a valid OData response with a value array
        secondDocument.RootElement.TryGetProperty("value", out var valueElement).Should().BeTrue();
        valueElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithInvalidDeltaToken_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$deltatoken=invalid-token-value");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithOutOfRangeDeltaTokenTicks_ReturnsBadRequest()
    {
        // Encodes payload: "9223372036854775807|0"
        const string outOfRangeToken = "OTIyMzM3MjAzNjg1NDc3NTgwN3ww";

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$deltatoken={outOfRangeToken}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithNegativeDeltaTokenLayerId_ReturnsBadRequest()
    {
        // Encodes payload: "1000|-1"
        const string negativeLayerToken = "MTAwMHwtMQ";

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$deltatoken={negativeLayerToken}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_DeltaLinkNotPresentOnIntermediatePages()
    {
        // Request with pagination that produces multiple pages
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // nextLink should be present (there are more pages)
        document.RootElement.TryGetProperty("@odata.nextLink", out _).Should().BeTrue();

        // deltaLink should NOT be present on intermediate pages
        document.RootElement.TryGetProperty("@odata.deltaLink", out _).Should().BeFalse(
            "delta links should only appear on the final page");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_DeltaTokenIsAllowedParameter()
    {
        // $deltatoken should be accepted as a valid query parameter (not rejected by parameter validation)
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$deltatoken=dGVzdHwx&$top=5");

        // May return BadRequest for invalid token format, but not for unknown parameter
        var content = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // If bad request, it should be about the token itself, not about the parameter name
            content.Should().NotContain("not allowed");
        }
    }
}
