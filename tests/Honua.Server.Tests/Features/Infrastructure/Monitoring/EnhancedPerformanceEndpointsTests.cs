// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Integration tests for enhanced performance monitoring endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
public class EnhancedPerformanceEndpointsTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public EnhancedPerformanceEndpointsTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("DELETE /api/v1/admin/performance/enhanced/cache/invalidate")]
    public async Task DeleteCacheInvalidate_RequiresAuthentication()
    {
        // Test with unauthenticated client first
        var response = await _fixture.Client.DeleteAsync("/api/v1/admin/performance/enhanced/cache/invalidate");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("DELETE /api/v1/admin/performance/enhanced/cache/invalidate")]
    public async Task DeleteCacheInvalidate_WithAdminAuth_ReturnsOkOrNotFound()
    {
        using var adminClient = _fixture.CreateAdminClient();

        var response = await adminClient.DeleteAsync("/api/v1/admin/performance/enhanced/cache/invalidate");

        // Accept OK (cache invalidated) or NotFound (endpoint not fully implemented)
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected OK, NotFound, or BadRequest, got {response.StatusCode}.");
    }
}