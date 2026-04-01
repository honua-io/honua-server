// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for Geoservices service import endpoints
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public class GeoservicesImportEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region POST /api/v1/admin/import/geoservices/discover

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithMissingServiceUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new { };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("ServiceUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithInvalidUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new { ServiceUrl = "not-a-valid-url" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("valid HTTPS");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithHttpScheme_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "http://example.com/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("valid HTTPS");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithEmbeddedCredentials_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://user:pass@example.com/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("embedded credentials");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithUnresolvableHost_ReturnsBadRequest()
    {
        // Arrange - use a URL that cannot be resolved in DNS
        var request = new
        {
            ServiceUrl = "https://nonexistent.arcgis.server.invalid/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        // Assert - unresolved hosts should be rejected by SSRF hardening
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("unresolvable");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    public async Task Discover_WithPrivateNetworkUrl_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://127.0.0.1/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/discover", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("private");
    }

    #endregion

    #region POST /api/v1/admin/import/geoservices/start

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithMissingServiceUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            LayerId = 0,
            TableName = "test_table"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("ServiceUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithMissingTableName_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("TableName is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithInvalidTableName_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "invalid-table-name!" // Contains invalid characters
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid table name");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithHttpScheme_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "http://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_http_scheme"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("valid HTTPS");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithValidRequest_ReturnsAccepted()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_geoservices_import"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        // Assert - should return 202 Accepted with job info
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobId");
        content.Should().Contain("statusUrl");
        content.Should().Contain("cancelUrl");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithPrivateNetworkUrl_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://127.0.0.1/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_private_url"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("private");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WhenDistributedCoordinationIsUnavailable_ReturnsServiceUnavailable()
    {
        await using var degradedFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IDistributedImportJobManager, DegradedImportJobManager>();
            });

        await degradedFixture.InitializeAsync();

        var response = await degradedFixture.Client.PostAsJsonAsync(
            "/api/v1/admin/import/geoservices/start",
            new
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
                LayerId = 0,
                TableName = "test_degraded_import"
            });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("coordination is unavailable");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_WithUnresolvableHost_ReturnsBadRequest()
    {
        var request = new
        {
            ServiceUrl = "https://nonexistent.arcgis.server.invalid/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_unresolvable_url"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("unresolvable");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task BackgroundWorker_WithUnsafeStoredServiceUrl_FailsJobBeforeImport()
    {
        using var scope = _fixture.Services.CreateScope();
        var jobManager = scope.ServiceProvider.GetRequiredService<IDistributedImportJobManager>();

        var jobId = Guid.NewGuid().ToString("N")[..12];
        var request = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = "https://127.0.0.1/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "unsafe_job_url",
            TargetSrid = 4326,
            OverwriteExisting = false,
            RequestTimeoutSeconds = 30,
            MaxRetries = 0,
            AutoPublish = false
        };

        var progress = GeoservicesImportProgress.CreateInitial(
            jobId,
            request.ServiceUrl,
            request.LayerId,
            request.TableName);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        GeoservicesImportProgress? latestProgress = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            latestProgress = await jobManager.ProgressStore.GetProgressAsync(jobId);
            if (latestProgress is { Status: GeoservicesImportStatus.Failed })
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        latestProgress.Should().NotBeNull();
        latestProgress!.Status.Should().Be(GeoservicesImportStatus.Failed);
        latestProgress.ErrorMessage.Should().Contain("not allowed");
    }

    #endregion

    #region GET /api/v1/admin/import/geoservices/jobs/{jobId}

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    public async Task GetJobStatus_WithNonExistentJob_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/geoservices/jobs/nonexistent123");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    public async Task GetJobStatus_AfterStartingJob_ReturnsProgress()
    {
        // Arrange - start a job first
        var startRequest = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_job_status"
        };

        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", startRequest);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var startContent = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startContent.GetProperty("jobId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/v1/admin/import/geoservices/jobs/{jobId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobId");
        content.Should().Contain("status");
        content.Should().Contain("tableName");
    }

    #endregion

    #region POST /api/v1/admin/import/geoservices/jobs/{jobId}/cancel

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/jobs/{jobId}/cancel")]
    public async Task CancelJob_WithNonExistentJob_ReturnsNotFound()
    {
        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/geoservices/jobs/nonexistent123/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/jobs/{jobId}/cancel")]
    public async Task CancelJob_AfterStartingJob_ReturnsSuccess()
    {
        // Arrange - start a job first
        var startRequest = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_cancel_job"
        };

        var startResponse = await _client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", startRequest);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var startContent = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startContent.GetProperty("jobId").GetString();

        // Act
        var response = await _client.PostAsync($"/api/v1/admin/import/geoservices/jobs/{jobId}/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(jobId!);
        content.Should().Contain("cancellation");
    }

    #endregion

    #region GET /api/v1/admin/import/geoservices/jobs

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs")]
    public async Task ListJobs_ReturnsJobsList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/geoservices/jobs");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobs");
    }

    #endregion
}

internal sealed class DegradedImportJobManager : IDistributedImportJobManager, IImportCoordinationHealth
{
    public IDistributedJobQueueService JobQueue { get; } = new NoopDistributedJobQueue();
    public IDistributedLeaderElection LeaderElection { get; } = new NoopDistributedLeaderElection();
    public IDistributedProgressStore<GeoservicesImportProgress> ProgressStore { get; } = new NoopProgressStore<GeoservicesImportProgress>();
    public IDistributedProgressStore<GeoservicesImportRequest> RequestStore { get; } = new NoopProgressStore<GeoservicesImportRequest>();
    public bool CanAcceptNewJobs => false;
}

internal sealed class NoopDistributedJobQueue : IDistributedJobQueueService
{
    public Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task CompleteAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RecoverInFlightAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
}

internal sealed class NoopDistributedLeaderElection : IDistributedLeaderElection
{
    public bool IsLeader => false;
    public string InstanceId => "noop";
    public Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NoopProgressStore<T> : IDistributedProgressStore<T> where T : class
{
    public Task SetProgressAsync(string jobId, T progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<T?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<T?>(null);
    public Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
