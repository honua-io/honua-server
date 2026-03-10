// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for GeoServer import endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public class GeoServerImportEndpointTests : IAsyncLifetime
{
    private readonly TestGeoServerImportService _importService = new(TimeSpan.FromMilliseconds(250));
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public GeoServerImportEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<IGeoServerImportService>(_importService);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/discover")]
    public async Task Discover_WithMissingGeoServerUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/discover", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("GeoServerRestUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/discover")]
    public async Task Discover_WithInvalidUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/discover", new
        {
            GeoServerRestUrl = "not-a-valid-url"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(GeoServerServiceUrlValidation.InvalidHttpsUrlMessage);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/discover")]
    public async Task Discover_WithValidUrl_ReturnsServiceInfo()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/discover", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var content = await response.Content.ReadFromJsonAsync<JsonDocument>();
        content.Should().NotBeNull();
        content!.RootElement.GetProperty("geoServerRestUrl").GetString().Should().Be("https://example.com/geoserver/rest");
        content.RootElement.GetProperty("version").GetString().Should().Be("2.28.0");
        content.RootElement.GetProperty("workspaces").GetArrayLength().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithMissingGeoServerUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            DryRun = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("GeoServerRestUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithoutDryRun_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = false
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Only dry-run imports are currently supported");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_WithValidDryRun_QueuesAndCompletesJob()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = true
        });

        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = await GetJobIdAsync(startResponse);

        var listResponse = await _client.GetAsync("/api/v1/admin/import/geoserver/jobs");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listResponse.Content.ReadAsStringAsync()).Should().Contain(jobId);

        var completed = await WaitForJobStatusAsync(jobId, "Completed", TimeSpan.FromSeconds(20));
        completed.RootElement.GetProperty("jobId").GetString().Should().Be(jobId);
        completed.RootElement.GetProperty("status").GetString().Should().Be("Completed");
        completed.RootElement.GetProperty("progress").GetProperty("currentPhase").GetString().Should().Be("Dry run completed");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoserver/jobs")]
    public async Task ListJobs_WithQueuedJob_ReturnsActiveJobs()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = true
        });

        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = await GetJobIdAsync(startResponse);

        var response = await _client.GetAsync("/api/v1/admin/import/geoserver/jobs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();
        payload!.RootElement.GetProperty("jobs").EnumerateArray()
            .Select(element => element.GetProperty("jobId").GetString())
            .Should().Contain(jobId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoserver/jobs/{jobId}")]
    public async Task GetJobStatus_WithNonExistentJob_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/geoserver/jobs/nonexistent123");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("GeoServer import job not found");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/jobs/{jobId}/cancel")]
    public async Task CancelJob_WithQueuedJob_ReturnsCancelled()
    {
        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = true
        });

        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = await GetJobIdAsync(startResponse);

        var cancelResponse = await _client.PostAsync($"/api/v1/admin/import/geoserver/jobs/{jobId}/cancel", null);
        cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var cancelled = await WaitForJobStatusAsync(jobId, "Cancelled", TimeSpan.FromSeconds(10));
        cancelled.RootElement.GetProperty("status").GetString().Should().Be("Cancelled");
    }

    private async Task<string> GetJobIdAsync(HttpResponseMessage response)
    {
        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return document!.RootElement.GetProperty("jobId").GetString()!;
    }

    private async Task<JsonDocument> WaitForJobStatusAsync(string jobId, string expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/v1/admin/import/geoserver/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
            var status = payload!.RootElement.GetProperty("status").GetString();
            if (status == expectedStatus)
            {
                return payload;
            }

            payload.Dispose();
            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out waiting for GeoServer import job '{jobId}' to reach status '{expectedStatus}'.");
    }

    private sealed class TestGeoServerImportService(TimeSpan delay) : IGeoServerImportService
    {
        public Task<GeoServerServiceInfo> DiscoverServiceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GeoServerServiceInfo
            {
                GeoServerRestUrl = request.GeoServerRestUrl,
                Version = "2.28.0",
                Workspaces =
                [
                    new GeoServerWorkspaceInfo
                    {
                        Name = "demo"
                    }
                ],
                DataStores =
                [
                    new GeoServerDataStoreInfo
                    {
                        Name = "states",
                        WorkspaceName = "demo",
                        Type = "PostGIS"
                    }
                ],
                Layers =
                [
                    new GeoServerLayerInfo
                    {
                        Name = "states",
                        WorkspaceName = "demo"
                    }
                ]
            });
        }

        public Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportConfigurationAsync(request, progress: null, cancellationToken);

        public async Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            IProgress<GeoServerImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            var current = GeoServerImportProgress.CreateInitial(
                request.JobId ?? Guid.NewGuid().ToString("N"),
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                estimatedTotalResources: 3,
                sourceGeoServerVersion: "2.28.0");
            progress?.Report(current);

            current = current with
            {
                Status = GeoServerImportStatus.Discovering,
                CurrentPhase = "Discovering GeoServer configuration",
                SourceGeoServerVersion = "2.28.0"
            };
            progress?.Report(current);

            await Task.Delay(delay, cancellationToken);

            current = current with
            {
                Status = GeoServerImportStatus.Completed,
                ResourcesProcessed = 3,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = request.DryRun ? "Dry run completed" : "Import completed successfully"
            };
            progress?.Report(current);

            return GeoServerImportResult.CreateSuccess(
                    request.GeoServerRestUrl,
                    request.TargetHonuaUrl,
                    workspacesImported: 1,
                    dataStoresImported: 1,
                    layersImported: 1,
                    sourceGeoServerVersion: "2.28.0",
                    wasDryRun: request.DryRun)
                with
            {
                FailedResources = 0
            };
        }
    }
}
