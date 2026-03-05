// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Tiles;

[Collection("Database")]
[Protocol(Protocols.Admin)]
public sealed class PmTilesExportEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /api/tiles/{layerId}/export?format=pmtiles")]
    public async Task StartPmTilesExport_WithValidRequest_ReturnsAccepted()
    {
        var response = await _fixture.Client.PostAsync(
            $"/api/tiles/{WebAppFixture.TestLayerId}/export?format=pmtiles",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        GetPropertyCaseInsensitive(json.RootElement, "jobId").GetString().Should().NotBeNullOrWhiteSpace();
        GetPropertyCaseInsensitive(json.RootElement, "format").GetString().Should().Be("pmtiles");
        GetPropertyCaseInsensitive(json.RootElement, "statusUrl").GetString()
            .Should().StartWith("/api/v1/admin/tile-operations/jobs/");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /api/tiles/{layerId}/export?format={format}")]
    public async Task StartPmTilesExport_WithUnsupportedFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/api/tiles/{WebAppFixture.TestLayerId}/export?format=mbtiles",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("format").And.Contain("pmtiles");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /api/tiles/{layerId}/export?format=pmtiles&bbox={bbox}")]
    public async Task StartPmTilesExport_WithInvalidBbox_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/api/tiles/{WebAppFixture.TestLayerId}/export?format=pmtiles&bbox=invalid,bbox",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("bbox");
    }

    private static JsonElement GetPropertyCaseInsensitive(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found.");
    }
}
