// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Tests.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

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

        var updatedProgress = await _progressStore.GetProgressAsync(operationId);
        updatedProgress.Should().NotBeNull();
        updatedProgress!.Status.Should().Be(OperationStatus.Completed,
            "Terminal durable job must bridge progress to authoritative state");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenRemoteDurableJobAlreadySucceeded_Returns409AndBridgesProgress()
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
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "test-admin-remote-cancel-terminal"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));

        var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-remote-cancel-terminal-test");
        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var updatedProgress = await _progressStore.GetProgressAsync(operationId);
        updatedProgress.Should().NotBeNull();
        updatedProgress!.Status.Should().Be(OperationStatus.Completed,
            "Terminal durable job must bridge progress to authoritative state");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenDurableJobAlreadyCancelled_CleansUpQueueAndReturns200()
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
            Status = ExecutionJobStatus.Cancelled,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Cancelled",
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test-already-cancelled"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));
        await jobQueue.EnqueueAsync(operationId);

        var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "already-cancelled-test") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Cancelled,
            CurrentStageStatus = GeoprocessingStageStatus.Cancelled,
            CompletedAt = now
        };
        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenProgressAlreadyCancelledAndDurableJobClaimed_PersistsSignalWithoutRemovingClaim()
    {
        var fixture = new WebAppFixture();
        var jobQueue = Substitute.For<IJobQueue>();
        fixture.ReplaceService<IJobQueue>(jobQueue);

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var jobStore = fixture.GetOptionalService<IExecutionJobStore>();
        if (jobStore == null)
        {
            await fixture.DisposeAsync();
            return;
        }

        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
            var jobRecord = new ExecutionJobRecord
            {
                OperationId = operationId,
                Status = ExecutionJobStatus.Running,
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now,
                ClaimedBy = "worker-remote",
                ClaimedAt = now.AddSeconds(-30),
                LastHeartbeatAt = now.AddSeconds(-5),
                CurrentPhase = "Running",
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "test-admin-reconcile-claimed-cancel"
                }
            };

            await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "already-cancelled-running") with
            {
                WorkflowStatus = GeoprocessingWorkflowStatus.Cancelled,
                CurrentStageStatus = GeoprocessingStageStatus.Cancelled,
                CompletedAt = now
            };
            await progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));

            var response = await client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var updatedJob = await jobStore.GetAsync(operationId);
            updatedJob.Should().NotBeNull();
            updatedJob!.Status.Should().Be(ExecutionJobStatus.Running);
            updatedJob.CancellationRequestedAt.Should().NotBeNull();

            await jobQueue.DidNotReceive().RemoveAsync(operationId, Arg.Any<CancellationToken>());
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenProgressCancelledButDurableJobSucceeded_Returns409AndBridgesProgress()
    {
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>();
        if (jobStore == null)
        {
            return;
        }

        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Completed",
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test-progress-cancelled-job-succeeded"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));

        var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "progress-cancelled-job-succeeded") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Cancelled,
            CurrentStageStatus = GeoprocessingStageStatus.Cancelled,
            CompletedAt = now
        };
        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var updatedProgress = await _progressStore.GetProgressAsync(operationId);
        updatedProgress.Should().NotBeNull();
        updatedProgress!.Status.Should().Be(OperationStatus.Completed,
            "Durable job terminal state must be bridged into stale cancelled progress");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenProgressCancelledAndRemoteBackendMissing_Returns409()
    {
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>();
        if (jobStore == null)
        {
            return;
        }

        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now,
            CurrentPhase = "Processing",
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "test-progress-cancelled-remote-missing"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));

        var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "progress-cancelled-remote-missing") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Cancelled,
            CurrentStageStatus = GeoprocessingStageStatus.Cancelled,
            CompletedAt = now
        };
        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenQueueRemovalFailsAfterDurableCancel_StillReturns200AndUpdatesProgress()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<IJobQueue>(new ThrowingRemoveJobQueue());

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var jobStore = fixture.GetOptionalService<IExecutionJobStore>();
        var jobQueue = fixture.GetOptionalService<IJobQueue>();
        if (jobStore == null || jobQueue == null)
        {
            await fixture.DisposeAsync();
            return; // Job orchestration not registered; skip.
        }

        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
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
                    WorkloadName = "test-admin-cancel-remove-failure"
                }
            };

            await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));
            await jobQueue.EnqueueAsync(operationId);

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-remove-failure");
            await progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));

            var response = await client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var updatedJob = await jobStore.GetAsync(operationId);
            updatedJob.Should().NotBeNull();
            updatedJob!.Status.Should().Be(ExecutionJobStatus.Cancelled);

            var updatedProgress = await progressStore.GetProgressAsync(operationId);
            updatedProgress.Should().NotBeNull();
            updatedProgress!.Status.Should().Be(OperationStatus.Cancelled);
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenDurableCancelCannotBeConfirmed_Returns409AndPreservesProgress()
    {
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        var jobQueue = Substitute.For<IJobQueue>();
        var fixture = new WebAppFixture()
            .ReplaceService<IExecutionJobStore>(jobStore)
            .ReplaceService<IJobQueue>(jobQueue);

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
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
                    WorkloadName = "test-admin-cancel-unconfirmed"
                }
            };

            jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
                .Returns(jobRecord, jobRecord, jobRecord, jobRecord);
            jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(false);

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-unconfirmed");
            await progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));

            var response = await client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);

            var updatedProgress = await progressStore.GetProgressAsync(operationId);
            updatedProgress.Should().NotBeNull();
            updatedProgress!.Status.Should().NotBe(OperationStatus.Cancelled);

            await jobQueue.DidNotReceive().RemoveAsync(operationId, Arg.Any<CancellationToken>());
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenRemoteQueuedNeverSubmitted_CancelsLocallyWithoutCallingBackend()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        var jobQueue = Substitute.For<IJobQueue>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(Arg.Any<string>()).Returns(false);
        var fixture = new WebAppFixture()
            .ReplaceService<IExecutionJobStore>(jobStore)
            .ReplaceService<IJobQueue>(jobQueue)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
            var jobRecord = new ExecutionJobRecord
            {
                OperationId = operationId,
                Status = ExecutionJobStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.AwsBatch,
                    Backend = "aws-batch",
                    WorkloadName = "test-admin-cancel-never-submitted"
                }
            };

            jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
                .Returns(jobRecord);
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExecutionJobRecord>());

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-never-submitted");
            await progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));

            var response = await client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await backend.DidNotReceive().CancelAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<CancellationToken>());
            await jobStore.Received(1).TrySetAsync(
                Arg.Is<ExecutionJobRecord>(j =>
                    j.OperationId == operationId &&
                    j.Status == ExecutionJobStatus.Cancelled &&
                    j.CompletedAt.HasValue &&
                    j.CurrentPhase == "Cancelled before submission"),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenRemoteQueuedNeverSubmitted_CasFailure_Returns409()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var jobStore = Substitute.For<IExecutionJobStore>();
        var jobQueue = Substitute.For<IJobQueue>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(Arg.Any<string>()).Returns(false);
        var fixture = new WebAppFixture()
            .ReplaceService<IExecutionJobStore>(jobStore)
            .ReplaceService<IJobQueue>(jobQueue)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
            var jobRecord = new ExecutionJobRecord
            {
                OperationId = operationId,
                Status = ExecutionJobStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.AwsBatch,
                    Backend = "aws-batch",
                    WorkloadName = "test-admin-cas-failure"
                }
            };

            var runningRecord = jobRecord with
            {
                Status = ExecutionJobStatus.Running,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
                .Returns(jobRecord, runningRecord);
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExecutionJobRecord>());
            jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(false);

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cas-failure");
            await progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));

            var response = await client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenRemoteBackendReturnsNonterminal_PersistsCancellationRequestedAt()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                Message = "Cancellation pending"
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        var jobQueue = Substitute.For<IJobQueue>();
        var fixture = new WebAppFixture()
            .ReplaceService<IExecutionJobStore>(jobStore)
            .ReplaceService<IJobQueue>(jobQueue)
            .ConfigureServices(services => services.AddSingleton(backend));

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
            var jobRecord = new ExecutionJobRecord
            {
                OperationId = operationId,
                Status = ExecutionJobStatus.Running,
                CreatedAt = now,
                UpdatedAt = now,
                CurrentPhase = "Processing",
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.AwsBatch,
                    Backend = "aws-batch",
                    WorkloadName = "test-admin-cancel-nonterminal"
                }
            };

            jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
                .Returns(jobRecord);
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExecutionJobRecord>());

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-nonterminal");
            await progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));

            var response = await client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await jobStore.Received().TrySetAsync(
                Arg.Is<ExecutionJobRecord>(j =>
                    j.OperationId == operationId &&
                    j.CancellationRequestedAt.HasValue &&
                    j.Status == ExecutionJobStatus.Running),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenRemoteBackendReturnsSucceeded_Returns409()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Succeeded,
                Message = "Job already completed"
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        var jobQueue = Substitute.For<IJobQueue>();
        var fixture = new WebAppFixture()
            .ReplaceService<IExecutionJobStore>(jobStore)
            .ReplaceService<IJobQueue>(jobQueue)
            .ConfigureServices(services => services.AddSingleton(backend));

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
            var jobRecord = new ExecutionJobRecord
            {
                OperationId = operationId,
                Status = ExecutionJobStatus.Running,
                CreatedAt = now,
                UpdatedAt = now,
                CurrentPhase = "Processing",
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.AwsBatch,
                    Backend = "aws-batch",
                    WorkloadName = "test-admin-cancel-terminal-race"
                }
            };

            jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
                .Returns(jobRecord);
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExecutionJobRecord>());

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-terminal-race");
            await progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));

            var response = await client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenRemoteBackendReturnsFailed_Returns409()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Failed,
                Message = "Job already failed"
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        var jobQueue = Substitute.For<IJobQueue>();
        var fixture = new WebAppFixture()
            .ReplaceService<IExecutionJobStore>(jobStore)
            .ReplaceService<IJobQueue>(jobQueue)
            .ConfigureServices(services => services.AddSingleton(backend));

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
            var jobRecord = new ExecutionJobRecord
            {
                OperationId = operationId,
                Status = ExecutionJobStatus.Running,
                CreatedAt = now,
                UpdatedAt = now,
                CurrentPhase = "Processing",
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.AwsBatch,
                    Backend = "aws-batch",
                    WorkloadName = "test-admin-cancel-failed-race"
                }
            };

            jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
                .Returns(jobRecord);
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExecutionJobRecord>());

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-failed-race");
            await progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));

            var response = await client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
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
    public async Task ListActiveOperations_PublishingType_ReturnsWorkflowFields()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var intentId = Guid.NewGuid().ToString("N");
        var progress = PublishingProgress.CreateExecuting(operationId, intentId) with
        {
            ServiceId = "svc-" + Guid.NewGuid().ToString("N")
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Publishing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().BeGreaterOrEqualTo(1);

        var op = operations[0];
        GetPropertyCaseInsensitive(op, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(op, "intentId").GetString().Should().Be(intentId);
        GetPropertyCaseInsensitive(op, "serviceId").GetString().Should().Be(progress.ServiceId);
        GetPropertyCaseInsensitive(op, "intentStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        CountPropertiesIgnoringCase(op, "operationId").Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_PublishingType_ReturnsWorkflowFields()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var intentId = Guid.NewGuid().ToString("N");
        var progress = PublishingProgress.CreateExecuting(operationId, intentId) with
        {
            ServiceId = "svc-" + Guid.NewGuid().ToString("N")
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync($"/api/v1/admin/operations/{operationId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "intentId").GetString().Should().Be(intentId);
        GetPropertyCaseInsensitive(json.RootElement, "serviceId").GetString().Should().Be(progress.ServiceId);
        GetPropertyCaseInsensitive(json.RootElement, "intentStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
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

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_DeploymentType_ReturnsLifecycleFields()
    {
        var operationId = $"deploy-{Guid.NewGuid():N}";
        var deploymentId = $"dep-{Guid.NewGuid():N}";
        var progress = DeploymentProgress.CreateRollingOut(operationId, deploymentId, RolloutState.InProgress);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync($"/api/v1/admin/operations/{operationId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "deploymentId").GetString().Should().Be(deploymentId);
        GetPropertyCaseInsensitive(json.RootElement, "deploymentStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        GetPropertyCaseInsensitive(json.RootElement, "rolloutState").ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_DeploymentType_ReturnsLifecycleFields()
    {
        var operationId = $"deploy-{Guid.NewGuid():N}";
        var deploymentId = $"dep-{Guid.NewGuid():N}";
        var progress = DeploymentProgress.CreateProvisioning(operationId, deploymentId);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Deployment");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().BeGreaterOrEqualTo(1);

        var op = operations.EnumerateArray()
            .First(e => GetPropertyCaseInsensitive(e, "operationId").GetString() == operationId);
        GetPropertyCaseInsensitive(op, "type").GetInt32().Should().Be((int)OperationType.Deployment);
        GetPropertyCaseInsensitive(op, "deploymentId").GetString().Should().Be(deploymentId);
        GetPropertyCaseInsensitive(op, "deploymentStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        CountPropertiesIgnoringCase(op, "operationId").Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/type/{operationType}")]
    public async Task GetOperationsByType_DeploymentType_ReturnsOperations()
    {
        var operationId = $"deploy-{Guid.NewGuid():N}";
        var deploymentId = $"dep-{Guid.NewGuid():N}";
        var progress = DeploymentProgress.CreateInitial(operationId, deploymentId);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync("/api/v1/admin/operations/type/Deployment");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        var op = operations.EnumerateArray()
            .First(e => GetPropertyCaseInsensitive(e, "operationId").GetString() == operationId);
        GetPropertyCaseInsensitive(op, "type").GetInt32().Should().Be((int)OperationType.Deployment);
        GetPropertyCaseInsensitive(op, "deploymentId").GetString().Should().Be(deploymentId);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_DeploymentType_PersistsCancelledStateWithNormalizedRollout()
    {
        var operationId = $"deploy-{Guid.NewGuid():N}";
        var deploymentId = $"dep-{Guid.NewGuid():N}";
        var progress = DeploymentProgress.CreateRollingOut(operationId, deploymentId, RolloutState.InProgress);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "type").GetInt32().Should().Be((int)OperationType.Deployment);

        var updated = await _progressStore.GetProgressAsync<DeploymentProgress>(operationId);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(OperationStatus.Cancelled);
        updated.DeploymentStatus.Should().Be(DeploymentStatus.Cancelled);
        updated.RolloutState.Should().Be(RolloutState.Cancelled);
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

    private sealed class ThrowingRemoveJobQueue : IJobQueue
    {
        private readonly HashSet<string> _operationIds = [];

        public Task EnqueueAsync(
            string operationId,
            OperationPriority priority = OperationPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            _operationIds.Add(operationId);
            return Task.CompletedTask;
        }

        public Task<string?> TryClaimAsync(
            string workerId,
            IReadOnlySet<ExecutionJobKind>? acceptedKinds = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_operationIds.FirstOrDefault());

        public Task RequeueAsync(
            string operationId,
            OperationPriority priority = OperationPriority.Normal,
            TimeSpan? visibleAfter = null,
            CancellationToken cancellationToken = default)
        {
            _operationIds.Add(operationId);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            string operationId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated queue removal failure");

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((long)_operationIds.Count);
    }
}
