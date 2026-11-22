// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
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
        data.TryGetProperty("fallbackEnabled", out _).Should().BeFalse();
        data.TryGetProperty("keyPrefix", out _).Should().BeTrue();
        data.TryGetProperty("defaultTtlSeconds", out _).Should().BeTrue();
        data.TryGetProperty("retryIntervalSeconds", out _).Should().BeFalse();
        data.TryGetProperty("generatedAt", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/cache/statistics")]
    public async Task GetCacheStatistics_ReturnsHitMissRatiosAndKeyCounts()
    {
        using var scope = _fixture.Services.CreateScope();
        var performanceMonitor = scope.ServiceProvider.GetRequiredService<IPerformanceMonitor>();
        performanceMonitor.RecordCacheMetrics("layer-catalog", "hit");
        performanceMonitor.RecordCacheMetrics("layer-catalog", "miss");
        performanceMonitor.RecordCacheMetrics("layer-catalog", "miss");

        var response = await _client.GetAsync("/api/v1/admin/operations/cache/statistics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("hits").GetInt64().Should().BeGreaterThanOrEqualTo(1);
        data.GetProperty("misses").GetInt64().Should().BeGreaterThanOrEqualTo(2);
        data.GetProperty("hitRatio").GetDouble().Should().BeGreaterThan(0);
        data.GetProperty("missRatio").GetDouble().Should().BeGreaterThan(0);
        data.TryGetProperty("localKeyCount", out _).Should().BeTrue();
        data.TryGetProperty("backend", out _).Should().BeTrue();
        data.GetProperty("types").TryGetProperty("layer-catalog", out var layerCatalog).Should().BeTrue();
        layerCatalog.TryGetProperty("hitRatio", out _).Should().BeTrue();
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
    public async Task PostInvalidate_WithAllScope_ReturnsSuccess()
    {
        var payload = new { scope = "all" };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/operations/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.GetProperty("invalidated").GetBoolean().Should().BeTrue();
        data.GetProperty("scope").GetString().Should().Be("all");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/cache/invalidate")]
    public async Task PostInvalidate_WithNoInput_Returns400()
    {
        var payload = new { };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/operations/cache/invalidate", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/cache/invalidate")]
    public async Task PostInvalidate_WithControlCharactersOrOversizedFields_Returns400()
    {
        var oversized = new string('a', 257);
        object[] payloads =
        [
            new { keyPattern = "test\r\n[ERR] FORGED" },
            new { keyPattern = "test\u2028FORGED" },
            new { serviceId = "svc\nFORGED" },
            new { scope = "all\r\nFORGED" },
            new { keyPattern = oversized },
            new { serviceId = oversized },
            new { scope = oversized },
        ];

        foreach (var payload in payloads)
        {
            var response = await _client.PostAsJsonAsync("/api/v1/admin/operations/cache/invalidate", payload);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("FORGED");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task PostAdminCacheInvalidate_WithControlCharactersOrUnknownScope_Returns400WithoutEchoingScope()
    {
        var oversized = new string('s', 257);
        object[] payloads =
        [
            new { scope = "bad\r\n[ERR] FORGED_CACHE_SCOPE" },
            new { scope = "bad\u2029FORGED_CACHE_SCOPE" },
            new { scope = new string(' ', 256) + "all" },
            new { scope = "<script>alert(1)</script>" },
            new { scope = "service", serviceId = "svc\r\nFORGED" },
            new { scope = "layer", serviceId = oversized, layerId = 0 },
            new { scope = "collection", collectionId = "col\nFORGED" },
            new { scope = "collection", collectionId = oversized },
        ];

        foreach (var payload in payloads)
        {
            var response = await _client.PostAsJsonAsync("/api/v1/admin/cache/invalidate", payload);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("FORGED");
            body.Should().NotContain("<script>");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task PostAdminCacheInvalidate_WithValidScope_ReturnsSuccess()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/cache/invalidate", new { scope = "OGC-Metadata" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("data").GetProperty("scope").GetString().Should().Be("ogc-metadata");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/cache/invalidate")]
    public async Task PostInvalidate_WhenCacheServiceThrows_ReturnsSanitizedError()
    {
        var cacheService = Substitute.For<ICacheService>();
        cacheService.RemoveByPatternAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("https://private-cache.invalid/secret"));

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<ICacheService>();
                services.AddSingleton(cacheService);
            });

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.CreateAdminClient().PostAsJsonAsync(
                "/api/v1/admin/operations/cache/invalidate",
                new { keyPattern = "test:*" });

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Cache invalidation failed.");
            body.Should().NotContain("https://private-cache.invalid/secret");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
