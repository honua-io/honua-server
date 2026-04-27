// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.OData.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Integration tests for OData $deltatoken change tracking support.
/// Verifies that change tracking is opt-in and delta links round-trip cleanly.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
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
    [InterfaceOperation(TestProtocols.ODataV4, "DeltaTracking")]
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
    public async Task Query_WithTrackChangesPaging_PreservesInitialSnapshotInFinalDeltaLink()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=2");
        request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var nextLink = document.RootElement.GetProperty("@odata.nextLink").GetString();
        nextLink.Should().NotBeNullOrWhiteSpace();
        nextLink.Should().Contain(ODataUtilityService.TrackChangesSnapshotParameter);

        var nextQuery = QueryHelpers.ParseQuery(new Uri(nextLink!).Query);
        var snapshotToken = nextQuery[ODataUtilityService.TrackChangesSnapshotParameter].ToString();
        snapshotToken.Should().NotBeNullOrWhiteSpace();
        ODataDeltaService.TryDecode(snapshotToken, out var snapshotState, out var snapshotError)
            .Should().BeTrue(snapshotError);

        string? deltaLink = null;
        var currentPathAndQuery = new Uri(nextLink!).PathAndQuery;
        for (var page = 0; page < 10; page++)
        {
            var pageResponse = await _fixture.Client.GetAsync(currentPathAndQuery);
            pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var pageContent = await pageResponse.Content.ReadAsStringAsync();
            using var pageDocument = JsonDocument.Parse(pageContent);

            if (pageDocument.RootElement.TryGetProperty("@odata.nextLink", out var pageNextLinkElement))
            {
                currentPathAndQuery = new Uri(pageNextLinkElement.GetString()!).PathAndQuery;
                continue;
            }

            pageDocument.RootElement.TryGetProperty("@odata.deltaLink", out var deltaLinkElement).Should().BeTrue();
            deltaLink = deltaLinkElement.GetString();
            break;
        }

        deltaLink.Should().NotBeNullOrWhiteSpace();
        var deltaQuery = QueryHelpers.ParseQuery(new Uri(deltaLink!).Query);
        var deltaToken = deltaQuery["$deltatoken"].ToString();
        deltaToken.Should().NotBeNullOrWhiteSpace();
        ODataDeltaService.TryDecode(deltaToken, out var finalState, out var finalError).Should().BeTrue(finalError);
        finalState.Timestamp.Should().Be(snapshotState.Timestamp);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_WithDeltaTokenPaging_CarriesStableUpperBoundUntilFinalDeltaLink()
    {
        var token = ODataDeltaService.Encode(new ODataDeltaService.DeltaQueryState
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            LayerId = TestLayerId
        });

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$deltatoken={token}&$top=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var nextLink = document.RootElement.GetProperty("@odata.nextLink").GetString();
        nextLink.Should().NotBeNullOrWhiteSpace();
        var nextQuery = QueryHelpers.ParseQuery(new Uri(nextLink!).Query);
        ODataDeltaService.TryDecode(nextQuery["$deltatoken"].ToString(), out var nextState, out var nextError)
            .Should().BeTrue(nextError);
        nextState.UpperBoundTimestamp.Should().NotBeNull();

        string currentPathAndQuery = new Uri(nextLink!).PathAndQuery;
        string? deltaLink = null;
        for (var page = 0; page < 10; page++)
        {
            var pageResponse = await _fixture.Client.GetAsync(currentPathAndQuery);
            pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var pageContent = await pageResponse.Content.ReadAsStringAsync();
            using var pageDocument = JsonDocument.Parse(pageContent);
            if (pageDocument.RootElement.TryGetProperty("@odata.nextLink", out var pageNextLinkElement))
            {
                currentPathAndQuery = new Uri(pageNextLinkElement.GetString()!).PathAndQuery;
                continue;
            }

            pageDocument.RootElement.TryGetProperty("@odata.deltaLink", out var deltaLinkElement).Should().BeTrue();
            deltaLink = deltaLinkElement.GetString();
            break;
        }

        deltaLink.Should().NotBeNullOrWhiteSpace();
        var deltaQuery = QueryHelpers.ParseQuery(new Uri(deltaLink!).Query);
        ODataDeltaService.TryDecode(deltaQuery["$deltatoken"].ToString(), out var finalState, out var finalError)
            .Should().BeTrue(finalError);
        finalState.Timestamp.Should().Be(nextState.UpperBoundTimestamp);
        finalState.UpperBoundTimestamp.Should().BeNull();
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
