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
/// Verifies that change tracking is opt-in and delta links round-trip cleanly.
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
    public async Task Query_WithoutTrackChangesPreference_DoesNotReturnDeltaLink()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.nextLink", out _).Should().BeFalse(
            "all results should fit in one page");
        document.RootElement.TryGetProperty("@odata.deltaLink", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(Protocols.ODataV4, "DeltaTracking")]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithTrackChangesPreference_ReturnsDeltaLinkAndPreferenceApplied()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=100");
        request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Preference-Applied", out var preferenceValues).Should().BeTrue();
        preferenceValues.Should().Contain("odata.track-changes");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.deltaLink", out var deltaLinkElement).Should().BeTrue();
        deltaLinkElement.GetString().Should().Contain("$deltatoken=");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithDeltaToken_ReturnsResults()
    {
        var firstRequest = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=100");
        firstRequest.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");

        var firstResponse = await _fixture.Client.SendAsync(firstRequest);
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

        secondDocument.RootElement.TryGetProperty("value", out var valueElement).Should().BeTrue();
        valueElement.ValueKind.Should().Be(JsonValueKind.Array);
        valueElement.GetArrayLength().Should().Be(0);
        secondDocument.RootElement.TryGetProperty("@odata.deltaLink", out _).Should().BeTrue();
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
        var request = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=5");
        request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.nextLink", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("@odata.deltaLink", out _).Should().BeFalse(
            "delta links should only appear on the final page");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithDeltaTokenAndAdditionalQueryOption_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$deltatoken=dGVzdHwx&$filter=ObjectId gt 1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
