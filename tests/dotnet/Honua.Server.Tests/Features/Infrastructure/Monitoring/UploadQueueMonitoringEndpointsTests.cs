// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Integration tests for upload queue monitoring endpoints.
/// </summary>
[Protocol(TestProtocols.Admin)]
public sealed class UploadQueueMonitoringEndpointsTests
{
    private const string AdminPassword = "upload-queue-monitoring-admin-key";

    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /monitoring/metrics/upload-queue")]
    public async Task UploadQueueMetrics_WithZeroMaxQueue_ReturnsFiniteUtilization()
    {
        using var factory = CreateFactory(maxQueuedUploads: 0);
        using var client = CreateAdminClient(factory);

        var response = await client.GetAsync("/monitoring/metrics/upload-queue");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("maxQueueDepth").GetInt32().Should().Be(0);
        root.GetProperty("queueUtilization").GetDouble().Should().Be(0.0);
        root.GetProperty("isHealthy").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /monitoring/health/comprehensive")]
    public async Task ComprehensiveHealth_WithZeroMaxQueue_ReturnsFiniteFileUploadUtilization()
    {
        using var factory = CreateFactory(maxQueuedUploads: 0);
        using var client = CreateAdminClient(factory);

        var response = await client.GetAsync("/monitoring/health/comprehensive");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fileUpload = document.RootElement.GetProperty("entries").GetProperty("file-upload");
        fileUpload.GetProperty("status").GetString().Should().Be("Unhealthy");

        var data = fileUpload.GetProperty("data");
        data.GetProperty("queueUtilization").GetDouble().Should().Be(0.0);
        data.GetProperty("queueUtilizationPercentage").GetString().Should().Be("0.00%");
    }

    private static WebApplicationFactory<Program> CreateFactory(int maxQueuedUploads)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword,
                        ["FileUpload:MaxQueuedUploads"] = maxQueuedUploads.ToString(CultureInfo.InvariantCulture)
                    });
                });
            });
    }

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        return client;
    }
}
