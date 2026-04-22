// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Honua.Server.Tests.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.OgcProcesses;

/// <summary>
/// Verifies that OGC job dismissal does not recreate a cancelled job from a stale
/// snapshot when the authoritative job record disappears between the initial read
/// and the post-notifier re-read.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.OgcApiProcesses)]
public sealed class OgcProcessesDismissJobTests : IAsyncLifetime
{
    private const string JobId = "geo-job-001";

    private readonly IExecutionJobStore _jobStore;
    private readonly IJobQueue _jobQueue;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IJobCancellationNotifier _cancellationNotifier;
    private readonly WebAppFixture _fixture;

    public OgcProcessesDismissJobTests()
    {
        _jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        _jobQueue = Substitute.For<IJobQueue>();
        _progressStore = Substitute.For<IUniversalProgressStore>();
        _cancellationNotifier = Substitute.For<IJobCancellationNotifier>();

        var queuedJob = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        _jobStore.GetAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(queuedJob, (ExecutionJobRecord?)null);
        _jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        _jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());

        _cancellationNotifier.Cancel(JobId).Returns(false);

        _fixture = new WebAppFixture()
            .ReplaceService(_jobStore)
            .ReplaceService(_jobQueue)
            .ReplaceService(_progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(_cancellationNotifier);
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_AlreadyCancelled_ReturnsOkWhenQueueRemovalFails()
    {
        var cancelledJob = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Cancelled,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        _jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(cancelledJob);
        _jobQueue.RemoveAsync(JobId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var response = await _fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_UnclaimedCasConflict_RetriesAndDismisses()
    {
        var queuedJob = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        _jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(queuedJob);

        // First CAS attempt fails (concurrent heartbeat), second succeeds
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false, true);

        var response = await _fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await _jobStore.Received(2).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.OperationId == JobId &&
                j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await _jobQueue.Received(1).RemoveAsync(JobId, Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_CasConflictRevealsSucceeded_Returns409()
    {
        var queuedJob = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        var succeededJob = queuedJob with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow
        };

        // Initial reads return queued; CAS re-read reveals succeeded
        _jobStore.GetAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(queuedJob, queuedJob, succeededJob);

        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _progressStore.Received().SetProgressAsync(
            JobId,
            Arg.Any<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingProgress>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_RemoteBackendReReadFindsSucceeded_Returns409WithoutCallingBackend()
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
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(JobId).Returns(false);

        var remoteQueued = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };
        var terminal = remoteQueued with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow
        };

        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(remoteQueued, terminal);
        jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());

        var fixture = new WebAppFixture()
            .ReplaceService(jobStore)
            .ReplaceService(jobQueue)
            .ReplaceService(progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);

            await backend.DidNotReceive().CancelAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<CancellationToken>());
            await progressStore.Received().SetProgressAsync(
                JobId,
                Arg.Any<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingProgress>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_RemoteQueuedNeverSubmitted_CancelsLocallyWithoutCallingBackend()
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
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(JobId).Returns(false);

        var remoteQueued = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(remoteQueued);
        jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());

        var fixture = new WebAppFixture()
            .ReplaceService(jobStore)
            .ReplaceService(jobQueue)
            .ReplaceService(progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await backend.DidNotReceive().CancelAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<CancellationToken>());
            await jobStore.Received(1).TrySetAsync(
                Arg.Is<ExecutionJobRecord>(job =>
                    job.OperationId == JobId &&
                    job.Status == ExecutionJobStatus.Cancelled &&
                    job.CompletedAt.HasValue &&
                    job.CurrentPhase == "Cancelled before submission"),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_RemoteQueuedRetryAwaitingResubmission_CancelsLocallyWithoutCallingBackend()
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
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(JobId).Returns(false);

        // Between-retries state: the prior submission failed, the reconciler requeued
        // the job and cleared ProviderOperationId, and NextRetryAt is still in the future.
        // No provider-side job exists to cancel, so we must short-circuit locally.
        var retryAwaitingResubmission = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 1,
            ProviderOperationId = null,
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(15),
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(retryAwaitingResubmission);
        jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());

        var fixture = new WebAppFixture()
            .ReplaceService(jobStore)
            .ReplaceService(jobQueue)
            .ReplaceService(progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await backend.DidNotReceive().CancelAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<CancellationToken>());
            await jobStore.Received(1).TrySetAsync(
                Arg.Is<ExecutionJobRecord>(job =>
                    job.OperationId == JobId &&
                    job.Status == ExecutionJobStatus.Cancelled &&
                    job.CompletedAt.HasValue &&
                    job.CurrentPhase == "Cancelled before submission"),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_RemoteQueuedNeverSubmitted_CasFailure_Returns409()
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
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(JobId).Returns(false);

        var remoteQueued = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        var runningRecord = remoteQueued with
        {
            Status = ExecutionJobStatus.Running,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(remoteQueued, remoteQueued, runningRecord);
        jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var fixture = new WebAppFixture()
            .ReplaceService(jobStore)
            .ReplaceService(jobQueue)
            .ReplaceService(progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);

            await backend.DidNotReceive().CancelAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_UnclaimedCasConflict_JobBecomesClaimed_SwitchesToDurableSignal()
    {
        var queuedJob = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        var claimedJob = queuedJob with
        {
            Status = ExecutionJobStatus.Running,
            ClaimedBy = "worker-remote-1",
            ClaimedAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };

        // Initial reads return unclaimed; CAS fails; re-read reveals claimed.
        _jobStore.GetAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(queuedJob, queuedJob, claimedJob);

        // First CAS fails (direct cancel), second succeeds (durable signal)
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false, true);

        var response = await _fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Must have written CancellationRequestedAt, not terminal Cancelled.
        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.OperationId == JobId &&
                j.CancellationRequestedAt.HasValue &&
                j.Status != ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        // Queue should NOT have been cleaned up — worker owns terminal state.
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_RemoteBackendNonterminalResponse_PersistsCancellationRequestedAt()
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
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(JobId).Returns(false);

        var remoteRunning = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(remoteRunning);
        jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());

        var fixture = new WebAppFixture()
            .ReplaceService(jobStore)
            .ReplaceService(jobQueue)
            .ReplaceService(progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await jobStore.Received().TrySetAsync(
                Arg.Is<ExecutionJobRecord>(j =>
                    j.OperationId == JobId &&
                    j.CancellationRequestedAt.HasValue &&
                    j.Status == ExecutionJobStatus.Running),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>());

            // Progress store must receive the nonterminal observation so clients polling
            // the progress projection see "Cancellation pending" immediately after dismiss.
            await progressStore.Received().SetProgressAsync(
                JobId,
                Arg.Is<GeoprocessingProgress>(p =>
                    p.CurrentPhase == "Cancellation pending" &&
                    p.WorkflowStatus == GeoprocessingWorkflowStatus.Running),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_RemoteBackendReturnsSucceeded_Returns409()
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
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(JobId).Returns(false);

        var remoteRunning = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(remoteRunning);
        jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());

        var fixture = new WebAppFixture()
            .ReplaceService(jobStore)
            .ReplaceService(jobQueue)
            .ReplaceService(progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_RemoteBackendReturnsFailed_Returns409()
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
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
        cancellationNotifier.Cancel(JobId).Returns(false);

        var remoteRunning = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(remoteRunning);
        jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());

        var fixture = new WebAppFixture()
            .ReplaceService(jobStore)
            .ReplaceService(jobQueue)
            .ReplaceService(progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(cancellationNotifier);
                services.AddSingleton(backend);
            });
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_ReReadFindsDeleted_Returns404WithoutRecreatingJob()
    {
        var response = await _fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should().Contain("no-such-job");

        await _jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _progressStore.DidNotReceive().SetProgressAsync(
            Arg.Any<string>(),
            Arg.Any<Honua.Core.Features.Infrastructure.Domain.IOperationProgress>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }
}
