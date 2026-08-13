// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin style suggestion endpoint.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class StyleSuggestionEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_ReturnsStyleSuggestion()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/suggest-style",
            new StyleSuggestionRequest());

        response.Be200Ok();

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            StyleSuggestionJsonContext.Default.ApiResponseStyleSuggestionResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.MapLibreStyle.Should().NotBeNull();
        apiResponse.Data!.MapLibreStyle!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        apiResponse.Data!.DrawingInfo.Should().NotBeNull();
        apiResponse.Data!.DrawingInfo!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        apiResponse.Data!.Legend.Should().NotBeNull();
        apiResponse.Data!.Legend!.Entries.Should().NotBeNullOrEmpty();
        apiResponse.Data!.Observations.Should().NotBeNullOrEmpty();
        apiResponse.Data!.Edition.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_WithInvalidLayerId_Returns404()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/metadata/layers/999999/suggest-style",
            new StyleSuggestionRequest());

        response.Be404NotFound();
    }

    // --- Depth pack (#2983): styling.auto-suggest contract assertions ---

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_ClassCountBeyondMaximum_ClampsLegendToTwelveClasses()
    {
        // ClassCount is clamped to [2, 12]; an out-of-range request must succeed
        // with a bounded legend rather than fail or produce 99 classes.
        var client = _fixture.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/suggest-style",
            new StyleSuggestionRequest { ClassCount = 99 });

        response.Be200Ok();
        var apiResponse = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            StyleSuggestionJsonContext.Default.ApiResponseStyleSuggestionResponse);

        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data!.Legend!.Entries.Should().NotBeEmpty();
        apiResponse.Data.Legend.Entries!.Length.Should().BeLessThanOrEqualTo(12);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_UnknownPreferredMethodAndPalette_DegradesGracefully()
    {
        // Unknown preference values are advisory: they must be ignored, not rejected.
        var client = _fixture.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/suggest-style",
            new StyleSuggestionRequest
            {
                PreferredMethod = "NotARealClassificationMethod",
                PreferredPalette = "NotARealPalette",
                PreferredField = "no_such_field"
            });

        response.Be200Ok();
        var apiResponse = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            StyleSuggestionJsonContext.Default.ApiResponseStyleSuggestionResponse);

        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data!.MapLibreStyle!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        apiResponse.Data.DrawingInfo!.Value.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_SuggestedDocuments_HonorDualFormatContract()
    {
        // The suggestion must emit BOTH projections of the same style: a MapLibre
        // version-8 document with at least one layer, and an Esri drawingInfo
        // document with a renderer — the dual-format contract the console relies on.
        var client = _fixture.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/suggest-style",
            new StyleSuggestionRequest());

        response.Be200Ok();
        var apiResponse = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            StyleSuggestionJsonContext.Default.ApiResponseStyleSuggestionResponse);

        var mapLibre = apiResponse!.Data!.MapLibreStyle!.Value;
        mapLibre.GetProperty("version").GetInt32().Should().Be(8);
        mapLibre.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0);

        var drawingInfo = apiResponse.Data.DrawingInfo!.Value;
        drawingInfo.TryGetProperty("renderer", out var renderer).Should().BeTrue();
        renderer.ValueKind.Should().Be(JsonValueKind.Object);
        renderer.TryGetProperty("type", out var rendererType).Should().BeTrue();
        rendererType.GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_SuggestedMapLibre_IsAcceptedByTheStyleUpdatePipeline()
    {
        // Round trip: the suggested MapLibre document must be applicable through the
        // real style-update surface (admin layer style PUT runs the MapLibre
        // normalizer), otherwise the suggestion capability hands out styles the
        // server itself rejects.
        var client = _fixture.CreateAdminClient();

        var suggestResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/suggest-style",
            new StyleSuggestionRequest());
        suggestResponse.Be200Ok();

        var apiResponse = JsonSerializer.Deserialize(
            await suggestResponse.Content.ReadAsStringAsync(),
            StyleSuggestionJsonContext.Default.ApiResponseStyleSuggestionResponse);
        var suggestedMapLibre = apiResponse!.Data!.MapLibreStyle!.Value;

        var updateRequest = new LayerStyleUpdateRequest { MapLibreStyle = suggestedMapLibre };
        using var content = JsonContent.Create(updateRequest, LayerStyleJsonContext.Default.LayerStyleUpdateRequest);
        var applyResponse = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            content);

        applyResponse.Be200Ok();
    }
}
