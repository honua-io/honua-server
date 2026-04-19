// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.ServiceDefaults;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ExecutionJobCancellationHelperTests
{
    [Fact]
    public async Task TryApplyBackendCancelAsync_IdenticalNonterminalObservation_IsNoOpAndDoesNotBumpUpdatedAt()
    {
        // Invariant: repeated remote cancel polls that observe the same nonterminal state
        // must not refresh UpdatedAt. If they did, the reconciler's missing-registration
        // grace window would never expire and orphaned jobs would stall forever.
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var cancellationRequestedAt = DateTimeOffset.UtcNow.AddMinutes(-9);
        var job = CreateJobRecord("job-noop", ExecutionJobStatus.Running) with
        {
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ClaimedBy = "worker-1",
            ClaimedAt = createdAt.AddMinutes(1),
            CancellationRequestedAt = cancellationRequestedAt,
            ProviderOperationId = "provider-op-1",
            PercentComplete = 42d,
            CurrentPhase = "Running"
        };
        var jobStore = new RecordingJobStore(job);

        var observation = new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Running,
            ProviderOperationId = "provider-op-1",
            PercentComplete = 42d,
            Message = "Running"
        };

        var transitions = ListenForTransitions();
        using var listener = transitions.Listener;

        var result = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
            jobStore, job, observation);

        result.Outcome.Should().Be(BackendCancelApplyOutcome.Applied);
        result.Job.Should().BeSameAs(job,
            "identical nonterminal observations must preserve the original record reference");
        jobStore.TrySetInvocations.Should().Be(0,
            "no-op observations must not touch the durable store — otherwise UpdatedAt gets refreshed");

        var stored = await jobStore.GetAsync("job-noop");
        stored!.UpdatedAt.Should().Be(updatedAt,
            "the durable UpdatedAt must not advance for idempotent cancel observations");
        stored.CancellationRequestedAt.Should().Be(cancellationRequestedAt);

        transitions.Events.Should().BeEmpty(
            "no status change occurred, so no transition metric should be emitted");
    }

    [Fact]
    public async Task TryApplyBackendCancelAsync_FirstNonterminalObservation_SetsCancellationRequestedAt()
    {
        // Initial idempotent remote cancel still needs to persist CancellationRequestedAt
        // so workers can observe the durable signal via heartbeat. This is the single
        // allowed UpdatedAt bump for the idempotent nonterminal path.
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var job = CreateJobRecord("job-first", ExecutionJobStatus.Running) with
        {
            UpdatedAt = updatedAt,
            ClaimedBy = "worker-2",
            CancellationRequestedAt = null,
            PercentComplete = 12d,
            CurrentPhase = "Running"
        };
        var jobStore = new RecordingJobStore(job);

        var observation = new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Running,
            PercentComplete = 12d,
            Message = "Running"
        };

        var result = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
            jobStore, job, observation);

        result.Outcome.Should().Be(BackendCancelApplyOutcome.Applied);
        result.Job.Should().NotBeSameAs(job);
        result.Job!.CancellationRequestedAt.Should().NotBeNull(
            "the initial observation writes the durable cancellation signal");
        result.Job.UpdatedAt.Should().BeAfter(updatedAt,
            "writing the cancellation signal is a real state change and must bump UpdatedAt");
        jobStore.TrySetInvocations.Should().Be(1);
    }

    [Fact]
    public async Task TryApplyBackendCancelAsync_TerminalCancelledObservation_PersistsAndEmitsTransitionMetric()
    {
        var job = CreateJobRecord("job-terminal", ExecutionJobStatus.Running) with
        {
            ClaimedBy = "worker-3",
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var jobStore = new RecordingJobStore(job);

        var observation = new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Cancelled,
            Message = "Cancelled by user"
        };

        var transitions = ListenForTransitions();
        using var listener = transitions.Listener;

        var result = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
            jobStore, job, observation);

        result.Outcome.Should().Be(BackendCancelApplyOutcome.Applied);
        result.Job!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        result.Job.CompletedAt.Should().NotBeNull(
            "terminal observations must stamp CompletedAt");
        result.Job.CurrentPhase.Should().Be("Cancelled by user");
        jobStore.TrySetInvocations.Should().Be(1);

        transitions.Events.Should().ContainSingle(t =>
            t.PreviousStatus == nameof(ExecutionJobStatus.Running)
            && t.Status == nameof(ExecutionJobStatus.Cancelled),
            "Running -> Cancelled is a real transition and must emit the counter");
    }

    [Fact]
    public async Task TryApplyBackendCancelAsync_TerminalConflict_ReturnsConflictWithFreshRecord()
    {
        // When the backend reports terminal Failed while we're trying to cancel, the
        // caller needs the fresh terminal state so it can report the conflict rather
        // than overwriting the finalized record.
        var job = CreateJobRecord("job-conflict", ExecutionJobStatus.Running) with
        {
            ClaimedBy = "worker-4"
        };
        var conflictingJob = job with
        {
            Status = ExecutionJobStatus.Failed,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "worker crashed"
        };
        var jobStore = new RecordingJobStore(conflictingJob) { RejectAllTrySet = true };

        var observation = new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Cancelled,
            Message = "Cancelled by user"
        };

        var result = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
            jobStore, job, observation);

        result.Outcome.Should().Be(BackendCancelApplyOutcome.TerminalConflict);
        result.TerminalStatus.Should().Be(ExecutionJobStatus.Failed);
        result.Job!.ErrorMessage.Should().Be("worker crashed");
    }

    [Fact]
    public async Task TryApplyBackendCancelAsync_MissingJob_ReturnsMissingOutcome()
    {
        var job = CreateJobRecord("job-missing", ExecutionJobStatus.Running);
        var jobStore = new RecordingJobStore() { RejectAllTrySet = true };

        var observation = new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Cancelled,
            Message = "Cancelled"
        };

        var result = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
            jobStore, job, observation);

        result.Outcome.Should().Be(BackendCancelApplyOutcome.Missing);
        result.Job.Should().BeNull();
    }

    [Fact]
    public void MergeBackendCancelObservation_IdenticalObservation_ReturnsOriginalRecordReference()
    {
        var now = DateTimeOffset.UtcNow;
        var job = CreateJobRecord("job-merge", ExecutionJobStatus.Running) with
        {
            ProviderOperationId = "provider-abc",
            PercentComplete = 75d,
            CurrentPhase = "Running",
            CancellationRequestedAt = now.AddMinutes(-1)
        };

        var observation = new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Running,
            ProviderOperationId = "provider-abc",
            PercentComplete = 75d,
            Message = "Running"
        };

        var merged = ExecutionJobCancellationHelper.MergeBackendCancelObservation(job, observation, now);

        merged.Should().BeSameAs(job,
            "the merge must return the same reference when all observable fields match — callers rely on ReferenceEquals to detect no-ops");
    }

    private static (MeterListener Listener, List<(string? Status, string? PreviousStatus)> Events) ListenForTransitions()
    {
        var events = new List<(string? Status, string? PreviousStatus)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HonuaTelemetry.ServiceName
                    && instrument.Name == "honua.execution.job.transitions_total")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            string? status = null;
            string? previous = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "honua.controlplane.execution.status")
                {
                    status = tag.Value as string;
                }
                else if (tag.Key == "honua.controlplane.execution.previous_status")
                {
                    previous = tag.Value as string;
                }
            }

            lock (events)
            {
                events.Add((status, previous));
            }
        });
        listener.Start();
        return (listener, events);
    }

    private static ExecutionJobRecord CreateJobRecord(
        string operationId,
        ExecutionJobStatus status) => new()
    {
        OperationId = operationId,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.AzureBatch,
            Backend = "azure-batch",
            WorkloadId = "plan-cancel",
            WorkloadName = "Geoprocessing"
        }
    };

    private sealed class RecordingJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs;

        public RecordingJobStore(params ExecutionJobRecord[] jobs)
        {
            _jobs = jobs.ToDictionary(job => job.OperationId, StringComparer.Ordinal);
        }

        public bool RejectAllTrySet { get; init; }

        public int TrySetInvocations { get; private set; }

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);

        public Task SetAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            TrySetInvocations++;
            if (RejectAllTrySet)
            {
                return Task.FromResult(false);
            }

            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values.ToArray());
    }
}
