// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

// Tile operations run as execution jobs, so these hosts compose the job store, queue and worker.
[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Tile)]
public sealed class TileOperationsEndpointsTests(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithProductionJobSubstrate();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await database.DataSource.ClearJobSubstrateAsync();
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

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task StartJob_WithInvalidOperation_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "not-a-real-operation",
            layerId = WebAppFixture.TestLayerId
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tile-operations/jobs/{jobId}")]
    public async Task GetJobStatus_NonexistentId_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/admin/tile-operations/jobs/{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs/{jobId}/cancel")]
    public async Task CancelJob_ActiveJob_ReturnsOk()
    {
        var jobId = await StartInvalidateJobAsync();
        var response = await _client.PostAsJsonAsync($"/api/v1/admin/tile-operations/jobs/{jobId}/cancel", new { });

        // Job may complete before cancel arrives (race), returning 400
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Accepted, HttpStatusCode.BadRequest);
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
        foreach (var property in element.EnumerateObject()
            .Where(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)))
        {
            return property.Value;
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found.");
    }
}

