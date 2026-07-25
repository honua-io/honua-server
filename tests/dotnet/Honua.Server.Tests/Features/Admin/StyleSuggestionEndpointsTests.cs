// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin style suggestion endpoint.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class StyleSuggestionEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture =
        new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_CommunityEdition_ReturnsGeometryDefaults()
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
        apiResponse.Data!.Edition.Should().Be("Community");
        apiResponse.Data!.SuggestedField.Should().BeNull();
        apiResponse.Data!.ClassificationMethod.Should().BeNull();
        apiResponse.Data!.PaletteName.Should().Be("Default");
        apiResponse.Data!.Observations.Should().Contain(
            observation => observation.Contains("Community edition", StringComparison.Ordinal));
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

/// <summary>
/// Pro-edition integration coverage for the full profiling and classification path.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class StyleSuggestionProEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture =
        new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/suggest-style")]
    public async Task SuggestStyle_ProEdition_ReturnsProfiledClassification()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/suggest-style",
            new StyleSuggestionRequest());

        response.Be200Ok();
        var apiResponse = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            StyleSuggestionJsonContext.Default.ApiResponseStyleSuggestionResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.Edition.Should().Be("Pro");
        apiResponse.Data!.SuggestedField.Should().NotBeNull();
        apiResponse.Data!.ClassificationMethod.Should().NotBeNullOrWhiteSpace();
        apiResponse.Data!.PaletteName.Should().NotBe("Default");
        apiResponse.Data!.Observations.Should().NotContain(
            observation => observation.Contains("Upgrade to Pro", StringComparison.Ordinal));
    }
}
