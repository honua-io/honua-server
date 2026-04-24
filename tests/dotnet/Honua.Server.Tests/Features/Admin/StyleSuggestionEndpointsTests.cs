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
}
