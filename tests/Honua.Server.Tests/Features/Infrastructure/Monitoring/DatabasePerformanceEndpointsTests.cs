// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Integration tests for database performance monitoring endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
public class DatabasePerformanceEndpointsTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public DatabasePerformanceEndpointsTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /api/v1/admin/performance/database/query-cache/statistics")]
    public async Task GetQueryCacheStatistics_ReturnsOkOrUnauthorized()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/admin/performance/database/query-cache/statistics");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected OK or Unauthorized, got {response.StatusCode}.");
    }
}
