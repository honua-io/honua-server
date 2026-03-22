// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class CacheOperationsEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/cache/health")]
    public async Task GetCacheHealth_ReturnsHealthStatus()
    {
        var response = await _client.GetAsync("/api/v1/admin/operations/cache/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.TryGetProperty("isHealthy", out _).Should().BeTrue();
        data.TryGetProperty("isUsingFallback", out _).Should().BeTrue();
        data.TryGetProperty("cacheEnabled", out _).Should().BeTrue();
        data.TryGetProperty("fallbackEnabled", out _).Should().BeTrue();
        data.TryGetProperty("keyPrefix", out _).Should().BeTrue();
        data.TryGetProperty("defaultTtlSeconds", out _).Should().BeTrue();
        data.TryGetProperty("retryIntervalSeconds", out _).Should().BeTrue();
        data.TryGetProperty("generatedAt", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/cache/invalidate")]
    public async Task PostInvalidate_WithKeyPattern_ReturnsSuccess()
    {
        var payload = new { keyPattern = "test:nonexistent:*" };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/operations/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.GetProperty("invalidated").GetBoolean().Should().BeTrue();
        data.GetProperty("scope").GetString().Should().Be("pattern");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/cache/invalidate")]
    public async Task PostInvalidate_WithServiceId_ReturnsSuccess()
    {
        var payload = new { serviceId = "test-service" };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/operations/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.GetProperty("invalidated").GetBoolean().Should().BeTrue();
        data.GetProperty("scope").GetString().Should().Be("service");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/cache/invalidate")]
    public async Task PostInvalidate_WithNoInput_Returns400()
    {
        var payload = new { };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/operations/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
