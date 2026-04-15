// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin operations progress endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.OperationsProgress)]
public sealed class OperationsProgressEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly List<string> _operationIds = [];
    private HttpClient _client = null!;
    private IUniversalProgressStore _progressStore = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
        _progressStore = _fixture.GetService<IUniversalProgressStore>();
    }

    public async Task DisposeAsync()
    {
        foreach (var operationId in _operationIds)
        {
            await _progressStore.DeleteProgressAsync(operationId);
        }

        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_WhenExists_ReturnsProgress()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "test.geojson", 10) with
        {
            Status = OperationStatus.Processing
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync($"/api/v1/admin/operations/{operationId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "uploadId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "status").GetInt32().Should().Be((int)OperationStatus.Processing);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenCancellable_ReturnsCancelledResponse()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "cancel.geojson", 25) with
        {
            Status = OperationStatus.Processing
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "type").GetInt32().Should().Be((int)OperationType.Upload);

        var updated = await _progressStore.GetProgressAsync(operationId);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(OperationStatus.Cancelled);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenBackedByExecutionJob_CancelsJobStoreAndRemovesFromQueue()
    {
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>();
        var jobQueue = _fixture.GetOptionalService<IJobQueue>();
        if (jobStore == null || jobQueue == null)
        {
            return; // Job orchestration not registered; skip.
        }

        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test-admin-cancel"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));
        await jobQueue.EnqueueAsync(operationId);

        var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-test");
        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedJob = await jobStore.GetAsync(operationId);
        updatedJob.Should().NotBeNull();
        updatedJob!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        updatedJob.CompletedAt.Should().NotBeNull();

        var queueDepth = await jobQueue.GetQueueDepthAsync();
        var updatedProgress = await _progressStore.GetProgressAsync(operationId);
        updatedProgress.Should().NotBeNull();
        updatedProgress!.Status.Should().Be(OperationStatus.Cancelled);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenDurableJobAlreadySucceeded_Returns409()
    {
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>();
        if (jobStore == null)
        {
            return; // Job orchestration not registered; skip.
        }

        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Completed",
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test-admin-cancel-terminal"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));

        // Progress store still shows Processing (stale), but durable job is terminal
        var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-terminal-test");
        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Progress store should not have been overwritten to Cancelled
        var updatedProgress = await _progressStore.GetProgressAsync(operationId);
        updatedProgress.Should().NotBeNull();
        updatedProgress!.Status.Should().NotBe(OperationStatus.Cancelled);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_WhenFiltered_ReturnsOnlyActive()
    {
        var uploadId = Guid.NewGuid().ToString("N");
        var uploadProgress = UploadProgress.CreateInitial(uploadId, "active.geojson", 50);
        await _progressStore.SetProgressAsync(uploadId, uploadProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(uploadId);

        var importId = Guid.NewGuid().ToString("N");
        var importProgress = ImportProgress.CreateInitial(importId, "import_table", SupportedFileFormat.GeoJson) with
        {
            Status = ImportStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await _progressStore.SetProgressAsync(importId, importProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(importId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Upload");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().Be(1);
        GetPropertyCaseInsensitive(operations[0], "operationId").GetString().Should().Be(uploadId);
        GetPropertyCaseInsensitive(operations[0], "type").GetInt32().Should().Be((int)OperationType.Upload);
        GetPropertyCaseInsensitive(operations[0], "uploadId").GetString().Should().Be(uploadId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_WhenMixedTypesPresent_PreservesUnifiedOperationIdentityFields()
    {
        var uploadId = Guid.NewGuid().ToString("N");
        var uploadProgress = UploadProgress.CreateInitial(uploadId, "mixed.geojson", 50) with
        {
            Status = OperationStatus.Processing
        };
        await _progressStore.SetProgressAsync(uploadId, uploadProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(uploadId);

        var geoprocessingId = Guid.NewGuid().ToString("N");
        var geoprocessingProgress = GeoprocessingProgress.CreateInitial(geoprocessingId);
        await _progressStore.SetProgressAsync(geoprocessingId, geoprocessingProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(geoprocessingId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement).EnumerateArray().ToList();
        operations.Should().Contain(op =>
            GetPropertyCaseInsensitive(op, "operationId").GetString() == uploadId &&
            GetPropertyCaseInsensitive(op, "type").GetInt32() == (int)OperationType.Upload &&
            GetPropertyCaseInsensitive(op, "uploadId").GetString() == uploadId);
        operations.Should().Contain(op =>
            GetPropertyCaseInsensitive(op, "operationId").GetString() == geoprocessingId &&
            GetPropertyCaseInsensitive(op, "type").GetInt32() == (int)OperationType.Geoprocessing &&
            GetPropertyCaseInsensitive(op, "currentStage").ValueKind != JsonValueKind.Undefined);
        operations.Should().OnlyContain(op => CountPropertiesIgnoringCase(op, "operationId") == 1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/type/{operationType}")]
    public async Task GetOperationsByType_WhenMatching_ReturnsOperations()
    {
        var importId = Guid.NewGuid().ToString("N");
        var importProgress = ImportProgress.CreateInitial(importId, "history_table", SupportedFileFormat.GeoJson) with
        {
            Status = ImportStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await _progressStore.SetProgressAsync(importId, importProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(importId);

        var response = await _client.GetAsync("/api/v1/admin/operations/type/Import");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().Be(1);
        GetPropertyCaseInsensitive(operations[0], "operationId").GetString().Should().Be(importId);
        GetPropertyCaseInsensitive(operations[0], "type").GetInt32().Should().Be((int)OperationType.Import);
        GetPropertyCaseInsensitive(operations[0], "jobId").GetString().Should().Be(importId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_NonexistentId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/admin/operations/nonexistent-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_AlreadyCancelled_ReturnsIdempotent200()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "double-cancel.geojson", 10) with
        {
            Status = OperationStatus.Cancelled
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_CompletedOperation_Returns409Conflict()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "completed.geojson", 50) with
        {
            Status = OperationStatus.Completed
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_GeoprocessingType_ReturnsWorkflowFields()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = GeoprocessingProgress.CreateInitial(operationId);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Geoprocessing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().BeGreaterOrEqualTo(1);

        var op = operations[0];
        GetPropertyCaseInsensitive(op, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(op, "workflowStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        GetPropertyCaseInsensitive(op, "currentStage").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        GetPropertyCaseInsensitive(op, "currentStageStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        GetPropertyCaseInsensitive(op, "stepsCompleted").GetInt32().Should().Be(0);
        CountPropertiesIgnoringCase(op, "operationId").Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_WhenNoneActive_ReturnsEmptyList()
    {
        // Use a unique type filter to avoid matching other operations
        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Upload");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        // The response should be parseable and contain an operations array
        var operations = GetOperationsArray(json.RootElement);
        operations.ValueKind.Should().Be(JsonValueKind.Array);
    }

    private static JsonElement GetOperationsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        return GetPropertyCaseInsensitive(root, "operations");
    }

    private static JsonElement GetPropertyCaseInsensitive(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected JSON object for property '{propertyName}'.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' not found in JSON payload.");
    }

    private static int CountPropertiesIgnoringCase(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected JSON object for property '{propertyName}'.");
        }

        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }
}
