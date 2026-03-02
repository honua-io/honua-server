// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Tile)]
public sealed class TileOperationsEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task StartJob_WhenInvalidateRequest_ReturnsAccepted()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "invalidate",
            layerId = WebAppFixture.TestLayerId
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        GetPropertyCaseInsensitive(json.RootElement, "jobId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tile-operations/jobs/{jobId}")]
    public async Task GetJobStatus_WhenExists_ReturnsProgress()
    {
        var jobId = await StartInvalidateJobAsync();
        var response = await _client.GetAsync($"/api/v1/admin/tile-operations/jobs/{jobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        GetPropertyCaseInsensitive(json.RootElement, "jobId").GetString().Should().Be(jobId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tile-operations/jobs")]
    public async Task ListJobs_ReturnsCollection()
    {
        var response = await _client.GetAsync("/api/v1/admin/tile-operations/jobs?activeOnly=false");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobs = GetPropertyCaseInsensitive(json.RootElement, "jobs");
        jobs.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs/{jobId}/cancel")]
    public async Task CancelJob_WhenCalled_ReturnsKnownStatus()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs/nonexistent/cancel", new { });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs/{jobId}/retry")]
    public async Task RetryJob_WhenFailed_ReturnsAccepted()
    {
        var failedJobId = await StartUnsupportedSeedJobAndWaitForFailureAsync();

        var retryResponse = await _client.PostAsJsonAsync($"/api/v1/admin/tile-operations/jobs/{failedJobId}/retry", new { });
        retryResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var json = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
        var retryJobId = GetPropertyCaseInsensitive(json.RootElement, "retryJobId").GetString();
        retryJobId.Should().NotBeNullOrWhiteSpace();
        retryJobId.Should().NotBe(failedJobId);
    }

    private async Task<string> StartInvalidateJobAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "invalidate",
            layerId = WebAppFixture.TestLayerId
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return GetPropertyCaseInsensitive(json.RootElement, "jobId").GetString()!;
    }

    private async Task<string> StartUnsupportedSeedJobAndWaitForFailureAsync()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "seed",
            layerId = WebAppFixture.TestLayerId,
            tileMatrixSetId = "UnsupportedSet"
        });

        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var jobId = GetPropertyCaseInsensitive(startJson.RootElement, "jobId").GetString()!;

        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeout)
        {
            var statusResponse = await _client.GetAsync($"/api/v1/admin/tile-operations/jobs/{jobId}");
            statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var statusJson = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            var status = (OperationStatus)GetPropertyCaseInsensitive(statusJson.RootElement, "status").GetInt32();
            if (status == OperationStatus.Failed)
            {
                return jobId;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Timed out waiting for tile seed job to fail.");
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

