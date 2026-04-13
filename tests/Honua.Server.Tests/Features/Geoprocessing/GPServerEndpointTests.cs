// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Integration tests for GPServer REST endpoints operating as a protocol adapter
/// over the canonical process runtime.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.GPServer)]
public sealed class GPServerEndpointTests : IAsyncLifetime
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

    // -----------------------------------------------------------------------
    // Service Info
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_ReturnsGPServerMetadata()
    {
        var response = await _client.GetAsync("/rest/services/TestService/GPServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("currentVersion").GetDouble().Should().Be(10.81);
        root.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeAsynchronous");
        root.TryGetProperty("tasks", out _).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Task Info
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_ReturnsTaskMetadata()
    {
        var response = await _client.GetAsync("/rest/services/TestService/GPServer/BufferAnalysis");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("name").GetString().Should().Be("BufferAnalysis");
        root.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeAsynchronous");
    }

    // -----------------------------------------------------------------------
    // Execute (sync — returns 501)
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task Execute_ReturnsNotImplemented()
    {
        var response = await _client.GetAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/execute?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExecutePost_ReturnsNotImplemented()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["input_features"] = "test"
        });

        var response = await _client.PostAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/execute", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    // -----------------------------------------------------------------------
    // SubmitJob
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ReturnsJobIdAndSubmittedStatus()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["input_features"] = "test-layer",
            ["buffer_distance"] = "100"
        });

        var response = await _client.PostAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/submitJob", content);

        // SubmitJob returns 202 with job info, or falls back to error if store is unavailable
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            root.GetProperty("jobId").GetString().Should().NotBeNullOrWhiteSpace();
            root.GetProperty("jobStatus").GetString().Should().Be("esriJobSubmitted");
        }
        else
        {
            // Without Redis, the store is unavailable — acceptable in integration tests
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJobGet_ReturnsJobIdOrServiceUnavailable()
    {
        var response = await _client.GetAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/submitJob?f=json&input=test");

        // GET submit follows the same pattern
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Accepted,
            HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Job Status
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithInvalidJobId_Returns404()
    {
        var response = await _client.GetAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/jobs/nonexistent-job-id?f=json");

        // Without Redis: ServiceUnavailable; With Redis: NotFound
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Job Result
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task JobResult_WithInvalidJobId_ReturnsError()
    {
        var response = await _client.GetAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/jobs/nonexistent/results/Output?f=json");

        // Without Redis: ServiceUnavailable; With Redis: NotFound
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Cancel Job
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_WithInvalidJobId_ReturnsError()
    {
        var response = await _client.GetAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/jobs/nonexistent/cancel?f=json");

        // Without Redis: ServiceUnavailable; With Redis: NotFound
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJobPost_WithInvalidJobId_ReturnsError()
    {
        var response = await _client.PostAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/jobs/nonexistent/cancel",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" }));

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Route binding validation
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithMismatchedService_ReturnsNotFound()
    {
        // Submit under TestService/BufferAnalysis
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["input_features"] = "test-layer"
        });

        var submitResponse = await _client.PostAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/submitJob", content);

        if (submitResponse.StatusCode != HttpStatusCode.Accepted)
        {
            // Without Redis, skip binding validation — store unavailable
            return;
        }

        var submitJson = await submitResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(submitJson);
        var jobId = doc.RootElement.GetProperty("jobId").GetString()!;

        // Query status under a different service — should be rejected
        var statusResponse = await _client.GetAsync(
            $"/rest/services/OtherService/GPServer/BufferAnalysis/jobs/{jobId}?f=json");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithMismatchedTask_ReturnsNotFound()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["input_features"] = "test-layer"
        });

        var submitResponse = await _client.PostAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/submitJob", content);

        if (submitResponse.StatusCode != HttpStatusCode.Accepted)
        {
            return;
        }

        var submitJson = await submitResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(submitJson);
        var jobId = doc.RootElement.GetProperty("jobId").GetString()!;

        // Query status under a different task — should be rejected
        var statusResponse = await _client.GetAsync(
            $"/rest/services/TestService/GPServer/DifferentTask/jobs/{jobId}?f=json");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------------
    // Cross-protocol binding rejection
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithNonGPServerJob_ReturnsNotFound()
    {
        // Write a job record directly to the store without GPServer binding metadata.
        // This simulates a gRPC-submitted job that should not be visible via GPServer routes.
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>();
        if (jobStore == null)
        {
            // Without Redis, skip — store unavailable
            return;
        }

        var jobId = $"grpc-test-{Guid.NewGuid():N}";
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "grpc-test"
                // No gpserver.serviceId / gpserver.taskName parameters
            }
        };

        var created = await jobStore.TryCreateAsync(jobRecord);
        if (!created)
        {
            return;
        }

        // Access via GPServer route — should be rejected (no GPServer binding metadata)
        var response = await _client.GetAsync(
            $"/rest/services/AnyService/GPServer/AnyTask/jobs/{jobId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------------
    // GP environment controls
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithEnvOutSR_ReturnsBadRequest()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["input_features"] = "test-layer",
            ["env:outSR"] = "4326"
        });

        var response = await _client.PostAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/submitJob", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJobGet_WithEnvProcessSR_ReturnsBadRequest()
    {
        var response = await _client.GetAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/submitJob?f=json&input=test&env:processSR=3857");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------------
    // Missing parameters
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WithMissingJobId_ReturnsBadRequestOrNotFound()
    {
        // Empty jobId in the route — the routing framework will either 404 or match
        var response = await _client.GetAsync(
            "/rest/services/TestService/GPServer/BufferAnalysis/jobs/?f=json");

        response.IsSuccessStatusCode.Should().BeFalse();
    }
}
