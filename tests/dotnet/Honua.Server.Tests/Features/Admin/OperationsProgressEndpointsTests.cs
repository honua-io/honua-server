// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Infrastructure.Progress;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Helpers;
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
[Protocol(TestProtocols.Admin)]
[Operation(Operations.OperationsProgress)]
public sealed partial class OperationsProgressEndpointsTests : IAsyncLifetime
{
    // Isolate durable operation state so cancel/retry assertions do not depend on other tests.
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ReplaceRequestService<IExecutionJobStore>(new InMemoryExecutionJobStore())
        .ReplaceRequestService<IJobQueue>(new InMemoryJobQueue());
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
        var jobStore = RequireRegisteredService(_fixture.GetOptionalService<IExecutionJobStore>());
        var jobQueue = RequireRegisteredService(_fixture.GetOptionalService<IJobQueue>());

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
        queueDepth.Should().Be(0, "cancelling the job must dequeue it so it is never dispatched to a worker");
        var updatedProgress = await _progressStore.GetProgressAsync(operationId);
        updatedProgress.Should().NotBeNull();
        updatedProgress!.Status.Should().Be(OperationStatus.Cancelled);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenLocalWorkerAcceptsSignal_PersistsRequestBeforeSignalling()
    {
        var jobs = new InMemoryExecutionJobStore();
        var notifier = Substitute.For<IJobCancellationNotifier>();
        var operationId = Guid.NewGuid().ToString("N");
        Task<ExecutionJobRecord?>? observedAtSignal = null;
        notifier.Cancel(operationId).Returns(_ =>
        {
            observedAtSignal = jobs.GetAsync(operationId);
            return true;
        });
        await using var fixture = new WebAppFixture()
            .ReplaceRequestService<IExecutionJobStore>(jobs)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(notifier);
            });
        await fixture.InitializeAsync();
        await jobs.TryCreateAsync(CancellationJob(operationId, ExecutionJobKind.Geoprocessing, ExecutionJobStatus.Running) with
        { ClaimedBy = "lost-worker", Version = 0 });
        await fixture.GetService<IUniversalProgressStore>().SetProgressAsync(operationId,
            GeoprocessingProgress.CreateForSubmittedJob(operationId, "cancel-in-flight") with
            { WorkflowStatus = GeoprocessingWorkflowStatus.Running });

        var response = await fixture.Client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        observedAtSignal.Should().NotBeNull();
        (await observedAtSignal!)!.CancellationRequestedAt.Should().NotBeNull(
            "a worker crash must not discard accepted cancellation");
        (await jobs.GetAsync(operationId))!.Status.Should().Be(ExecutionJobStatus.Running,
            "the worker owns the terminal transition");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenDurableJobAlreadySucceeded_Returns409()
    {
        var jobStore = RequireRegisteredService(_fixture.GetOptionalService<IExecutionJobStore>());

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
        var jobStore = RequireRegisteredService(_fixture.GetOptionalService<IExecutionJobStore>());

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
        var jobStore = RequireRegisteredService(_fixture.GetOptionalService<IExecutionJobStore>());
        var jobQueue = RequireRegisteredService(_fixture.GetOptionalService<IJobQueue>());

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
        var fixture = new WebAppFixture()
            .ReplaceService<IExecutionJobStore>(new InMemoryExecutionJobStore());
        var jobQueue = Substitute.For<IJobQueue>();
        fixture.ReplaceService<IJobQueue>(jobQueue);

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var jobStore = RequireRegisteredService(fixture.GetOptionalService<IExecutionJobStore>());

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
    public async Task CancelOperation_WhenProgressAlreadyCancelledAndDurableJobNonterminal_BridgesProgressToNonterminal()
    {
        var jobStore = RequireRegisteredService(_fixture.GetOptionalService<IExecutionJobStore>());

        var operationId = $"gp-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            ClaimedBy = "worker-local",
            ClaimedAt = now.AddSeconds(-30),
            LastHeartbeatAt = now.AddSeconds(-5),
            CurrentPhase = "Running",
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test-reconcile-bridges-progress"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, TimeSpan.FromMinutes(5));

        var staleProgress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "stale-cancelled-progress") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Cancelled,
            CurrentStageStatus = GeoprocessingStageStatus.Cancelled,
            CompletedAt = now
        };
        await _progressStore.SetProgressAsync(operationId, staleProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync($"/api/v1/admin/operations/{operationId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedProgress = await _progressStore.GetProgressAsync(operationId);
        updatedProgress.Should().NotBeNull();
        updatedProgress!.Status.Should().Be(OperationStatus.Processing,
            "nonterminal durable job must be bridged over stale Cancelled progress so admin polling reflects the live state");
        updatedProgress.CompletedAt.Should().BeNull("bridging a nonterminal job must clear stale terminal timestamps");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenProgressCancelledButDurableJobSucceeded_Returns409AndBridgesProgress()
    {
        var jobStore = RequireRegisteredService(_fixture.GetOptionalService<IExecutionJobStore>());

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
        var jobStore = RequireRegisteredService(_fixture.GetOptionalService<IExecutionJobStore>());

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
            .ReplaceService<IExecutionJobStore>(new InMemoryExecutionJobStore())
            .ReplaceService<IJobQueue>(new ThrowingRemoveJobQueue());

        await fixture.InitializeAsync();

        var client = fixture.Client;
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        var jobStore = RequireRegisteredService(fixture.GetOptionalService<IExecutionJobStore>());
        var jobQueue = RequireRegisteredService(fixture.GetOptionalService<IJobQueue>());

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
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
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

    // Regression: a remote job the reconciler re-queued for retry sits at
    // Queued + AttemptCount > 0 + NextRetryAt != null + ProviderOperationId == null. The
    // admin cancel surface must treat that between-retries state as pre-submission and must
    // not invoke backend.CancelAsync against the stale (possibly TTL-cleaned) prior attempt.
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenRemoteQueuedRetryAwaitingResubmission_CancelsLocallyWithoutCallingBackend()
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
                AttemptCount = 1,
                ProviderOperationId = null,
                NextRetryAt = now.AddSeconds(15),
                CurrentPhase = "Retrying (attempt 2/3)",
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.AwsBatch,
                    Backend = "aws-batch",
                    WorkloadName = "test-admin-cancel-retry-backoff"
                }
            };

            jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
                .Returns(jobRecord);
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExecutionJobRecord>());

            var progress = GeoprocessingProgress.CreateForSubmittedJob(operationId, "admin-cancel-retry-backoff");
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
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
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
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
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

            var polled = await progressStore.GetProgressAsync<GeoprocessingProgress>(operationId);
            polled.Should().NotBeNull();
            polled!.CurrentPhase.Should().Be("Cancellation pending");
            polled.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Running);
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
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
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
            jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
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
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_QueuedExportRetryWithCompletedProgress_CancelsDurableJob()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var jobStore = _fixture.GetService<IExecutionJobStore>();
        var now = DateTimeOffset.UtcNow;
        await jobStore.TryCreateAsync(new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Export,
                Backend = "local",
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                WorkloadName = "export-retry"
            }
        });
        await _progressStore.SetProgressAsync(operationId,
            ExportProgress.CreateInitial(operationId, "csv", "svc", 1, 10) with
            { Status = OperationStatus.Completed, DownloadUrl = "https://example.test/deleted.csv" });
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await jobStore.GetAsync(operationId))!.Status.Should().Be(ExecutionJobStatus.Cancelled);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_RetryAfterCancelledProjection_DoesNotCancelSuccessor()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var initial = CancellationJob(operationId, ExecutionJobKind.Export, ExecutionJobStatus.Cancelled);
        var current = initial;
        var store = Substitute.For<IExecutionJobStore>();
        var reads = 0;
        store.GetAsync(operationId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var observed = current;
            if (++reads == 1)
            {
                current = initial with { Version = 2, ManualRetryGeneration = 1, Status = ExecutionJobStatus.Queued, CurrentPhase = "Queued for manual retry" };
            }

            return observed;
        });
        SetCurrentOnWrite(store, candidate => current = candidate);
        var queue = Substitute.For<IJobQueue>();
        var fixture = new WebAppFixture()
            .ReplaceRequestService<IExecutionJobStore>(store)
            .ReplaceRequestService<IJobQueue>(queue);
        await fixture.InitializeAsync();
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        try
        {
            await progressStore.SetProgressAsync(operationId,
                ExportProgress.CreateInitial(operationId, "csv", "svc", 1, 10));

            var response = await fixture.Client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            current.Status.Should().Be(ExecutionJobStatus.Queued);
            await queue.DidNotReceive().RemoveAsync(operationId, Arg.Any<CancellationToken>());
            await store.DidNotReceive().TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_LocalRetryAfterDurableCancel_DoesNotCancelSuccessor()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var initial = CancellationJob(operationId, ExecutionJobKind.Export, ExecutionJobStatus.Queued);
        var current = initial;
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(operationId, Arg.Any<CancellationToken>()).Returns(_ => current);
        SetCurrentOnWrite(store, candidate => current = candidate);
        var queue = Substitute.For<IJobQueue>();
        queue.RemoveAsync(operationId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            current = initial with { Version = 3, CurrentPhase = "Queued for manual retry" };
            return Task.CompletedTask;
        });
        var fixture = new WebAppFixture()
            .ReplaceRequestService<IExecutionJobStore>(store)
            .ReplaceRequestService<IJobQueue>(queue);
        await fixture.InitializeAsync();
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        try
        {
            await progressStore.SetProgressAsync(operationId,
                ExportProgress.CreateInitial(operationId, "csv", "svc", 1, 10));

            var response = await fixture.Client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            current.Status.Should().Be(ExecutionJobStatus.Queued);
            await store.Received(1).TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData(ExecutionJobStatus.Queued)]
    [InlineData(ExecutionJobStatus.Running)]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_RemoteTileRace_ReleasesAdmissionOnce(ExecutionJobStatus status)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var operation = $"cancel-{operationId}";
        var initial = RemoteTileCancellationJob(operationId, operation, status);
        var race = new ConcurrentCancellationStore(initial);
        var store = race.Store;
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { SupportsCancellation = true });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation { Status = ExecutionJobStatus.Cancelled });
        var fixture = new WebAppFixture()
            .ReplaceRequestService<IExecutionJobStore>(store)
            .ReplaceRequestService<IJobQueue>(Substitute.For<IJobQueue>())
            .ConfigureServices(services => services.AddSingleton(backend));
        await fixture.InitializeAsync();
        var progressStore = fixture.GetService<IUniversalProgressStore>();
        using var released = new TileQueueDepthObserver(operation);
        try
        {
            await progressStore.SetProgressAsync(operationId,
                TileOperationProgress.CreateInitial(operationId, operation, null, null, null));
            var responses = await Task.WhenAll(
                fixture.Client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null),
                fixture.Client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null));
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            race.Current.Status.Should().Be(ExecutionJobStatus.Cancelled);
            race.Writers.Should().Be(2, "both requests race the same nonterminal durable record");
            released.Value.Should().Be(-1, "only the terminal CAS winner releases tile admission");
        }
        finally
        {
            await progressStore.DeleteProgressAsync(operationId);
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_UnclaimedTileJob_ReleasesAdmissionOnce()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var operation = $"cancel-{operationId}";
        using var balance = new TileQueueDepthObserver(operation);
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = operationId, Status = ExecutionJobStatus.Queued, CreatedAt = now, UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.TileCache, Backend = "local",
                TargetKind = BatchComputeTargetKind.KubernetesJob, WorkloadName = "tile-cancel",
                Parameters = new Dictionary<string, string> { ["operation"] = operation }
            }
        };
        await _fixture.GetService<IExecutionJobStore>().TryCreateAsync(job);
        await _progressStore.SetProgressAsync(operationId,
            TileOperationProgress.CreateInitial(operationId, operation, null, null, null));
        _operationIds.Add(operationId);
        Honua.Server.Features.Admin.TileOperations.TileOperationMetrics.RecordJobQueued(job);

        (await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        balance.Value.Should().Be(0, "only the winning terminal transition releases tile admission");
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

    private static ExecutionJobRecord RemoteTileCancellationJob(string operationId, string operation, ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = status,
            ProviderOperationId = status == ExecutionJobStatus.Running ? "provider-operation" : null,
            CancellationRequestedAt = status == ExecutionJobStatus.Running ? now : null,
            CreatedAt = now, UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.TileCache, Backend = "aws-batch",
                TargetKind = BatchComputeTargetKind.AwsBatch, WorkloadName = "tile-cancel-race",
                Parameters = new Dictionary<string, string> { [TileCacheJobParameterKeys.Operation] = operation }
            }
        };
    }

    private static void SetCurrentOnWrite(IExecutionJobStore store, Action<ExecutionJobRecord> setCurrent)
    {
        store.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { setCurrent(call.Arg<ExecutionJobRecord>()); return true; });
    }

    private static ExecutionJobRecord CancellationJob(string operationId, ExecutionJobKind kind, ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId, Version = 1, Status = status,
            CreatedAt = now, UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = kind, Backend = "local",
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                WorkloadName = kind == ExecutionJobKind.Geoprocessing ? "cancel-in-flight" : "cancel-retry"
            }
        };
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

        foreach (var property in element.EnumerateObject()
            .Where(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)))
        {
            return property.Value;
        }

        throw new KeyNotFoundException($"Property '{propertyName}' not found in JSON payload.");
    }

    private static int CountPropertiesIgnoringCase(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected JSON object for property '{propertyName}'.");
        }

        return element.EnumerateObject()
            .Count(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static T RequireRegisteredService<T>(T? service)
        where T : class
    {
        service.Should().NotBeNull($"{typeof(T).Name} must be registered for durable operation endpoint coverage");
        return service!;
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
            IReadOnlySet<string>? acceptedRuntimeProfiles = null,
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
