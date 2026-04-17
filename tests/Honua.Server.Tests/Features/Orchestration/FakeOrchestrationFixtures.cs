// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Server.Tests.Features.Orchestration;

/// <summary>
/// In-memory test doubles for orchestration unit tests. Avoid I/O and keep behavior
/// predictable under concurrent reconciliation ticks.
/// </summary>
internal sealed class FakeWorkflowRunStore : IWorkflowRunStore
{
    private readonly ConcurrentDictionary<string, WorkflowRun> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

    public IReadOnlyCollection<WorkflowRun> Snapshot => _runs.Values.ToArray();

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => Task.FromResult(_leases.TryAdd(operationId, ownerId));

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => Task.FromResult(_leases.TryGetValue(operationId, out var current) && current == ownerId);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
    {
        _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
        return Task.CompletedTask;
    }

    /// <summary>
    /// When set, the next <see cref="TryCreateAsync"/> call raises this exception and
    /// clears itself. Used to simulate a transient store failure so the scheduler's
    /// retry-preserving behaviour can be exercised without a live Redis.
    /// </summary>
    public Exception? NextTryCreateFailure { get; set; }

    public Task<bool> TryCreateAsync(WorkflowRun run, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        if (NextTryCreateFailure is { } failure)
        {
            NextTryCreateFailure = null;
            throw failure;
        }

        return Task.FromResult(_runs.TryAdd(run.RunId, run));
    }

    public Task<WorkflowRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);

    public Task SetAsync(WorkflowRun run, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        _runs[run.RunId] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowRun>> ListActiveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowRun>>(
            _runs.Values
                .Where(r => !IsReconcileComplete(r))
                .ToArray());

    private static bool IsReconcileComplete(WorkflowRun run)
        => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled
            && run.StepStates.All(s => s.Status is WorkflowStepStatus.Succeeded
                or WorkflowStepStatus.Failed
                or WorkflowStepStatus.Cancelled
                or WorkflowStepStatus.Skipped);

    public Task<IReadOnlyList<WorkflowRun>> ListByWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowRun>>(
            _runs.Values.Where(r => string.Equals(r.WorkflowId, workflowId, StringComparison.Ordinal)).ToArray());
}

internal sealed class FakeWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly ConcurrentDictionary<string, WorkflowDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _scheduleClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _scheduleCursors = new(StringComparer.Ordinal);

    public Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
        => Task.FromResult(_definitions.TryGetValue(workflowId, out var d) ? d : null);

    public Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
        => Task.FromResult(_definitions.TryAdd(definition.WorkflowId, definition));

    public Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        _definitions[definition.WorkflowId] = definition;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowDefinition>>(_definitions.Values.ToArray());

    public Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowDefinition>>(
            _definitions.Values
                .Where(def => def.Trigger is { Kind: WorkflowTriggerKind.Cron, Enabled: true } &&
                              !string.IsNullOrWhiteSpace(def.Trigger.CronExpression))
                .ToArray());

    public Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var removed = _definitions.TryRemove(workflowId, out _);
        _scheduleCursors.TryRemove(workflowId, out _);
        return Task.FromResult(removed);
    }

    public Task<bool> TryClaimScheduleFireAsync(
        string workflowId,
        DateTimeOffset fireTime,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        _ = retention;
        return Task.FromResult(_scheduleClaims.TryAdd(BuildClaimKey(workflowId, fireTime), 0));
    }

    public Task ReleaseScheduleClaimAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default)
    {
        _scheduleClaims.TryRemove(BuildClaimKey(workflowId, fireTime), out _);
        return Task.CompletedTask;
    }

    private static string BuildClaimKey(string workflowId, DateTimeOffset fireTime)
    {
        var minute = fireTime.ToUniversalTime().ToUnixTimeSeconds() / 60L;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{workflowId}:{minute}");
    }

    public Task<DateTimeOffset?> GetScheduleCursorAsync(string workflowId, CancellationToken cancellationToken = default)
        => Task.FromResult(_scheduleCursors.TryGetValue(workflowId, out var cursor) ? cursor : (DateTimeOffset?)null);

    /// <summary>
    /// When set, <see cref="AdvanceScheduleCursorAsync"/> raises this exception on the next
    /// call and clears itself. Used to exercise the pending-cursor retry path.
    /// </summary>
    public Exception? NextAdvanceCursorFailure { get; set; }

    public Task AdvanceScheduleCursorAsync(string workflowId, DateTimeOffset fireTime, CancellationToken cancellationToken = default)
    {
        if (NextAdvanceCursorFailure is { } failure)
        {
            NextAdvanceCursorFailure = null;
            throw failure;
        }

        var candidate = fireTime.ToUniversalTime();
        _scheduleCursors.AddOrUpdate(
            workflowId,
            _ => candidate,
            (_, existing) => existing >= candidate ? existing : candidate);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test-only helper: pre-seed the durable scheduler cursor so harnesses can simulate
    /// the state a prior process left behind before a restart.
    /// </summary>
    public void SeedScheduleCursor(string workflowId, DateTimeOffset cursor)
        => _scheduleCursors[workflowId] = cursor.ToUniversalTime();
}

internal sealed class FakeProgressStore : IUniversalProgressStore
{
    private readonly ConcurrentDictionary<string, IOperationProgress> _progress = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IOperationProgress> Snapshot => _progress;

    public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        _progress[operationId] = progress;
        return Task.CompletedTask;
    }

    public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
        => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p as TProgress : null);

    public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p : null);

    public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        _progress.TryRemove(operationId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(_progress.Keys.ToArray());

    public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
        => Task.FromResult<IReadOnlyList<TProgress>>(
            _progress.Values.OfType<TProgress>().Where(p => p.Type == operationType).ToArray());
}

internal sealed class FakeWorkflowJobExecutor : IWorkflowJobExecutor
{
    private readonly ConcurrentDictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AnalysisResultPackage> _results = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AnalysisPlan> _submitted = [];
    private readonly List<IReadOnlyDictionary<string, string>?> _submittedMetadata = [];
    private readonly List<string> _cancelled = [];
    private int _seq;

    public Func<AnalysisPlan, string?, ExecutionJobRecord>? OnSubmit { get; set; }

    /// <summary>
    /// When set, <see cref="GetJobAsync"/> raises this exception on the first call and clears
    /// itself so subsequent reconcile ticks can succeed. Used to verify that transient
    /// observation failures do not terminalise a workflow step.
    /// </summary>
    public Exception? NextGetJobFailure { get; set; }

    /// <summary>
    /// When set, <see cref="CancelJobAsync"/> raises this exception on the next call so
    /// cascade-cancel warning behavior can be exercised.
    /// </summary>
    public Exception? NextCancelJobFailure { get; set; }

    /// <summary>
    /// When set, <see cref="GetJobResultsAsync"/> raises this exception on the next call
    /// and clears itself. Lets tests exercise the "job succeeded but result package is
    /// unavailable" transient path without hitting a real store.
    /// </summary>
    public Exception? NextGetJobResultsFailure { get; set; }

    public IReadOnlyList<AnalysisPlan> Submitted
    {
        get
        {
            lock (_submitted)
            {
                return _submitted.ToArray();
            }
        }
    }

    public IReadOnlyList<IReadOnlyDictionary<string, string>?> SubmittedMetadata
    {
        get
        {
            lock (_submitted)
            {
                return _submittedMetadata.ToArray();
            }
        }
    }

    public IReadOnlyList<string> Cancelled
    {
        get
        {
            lock (_cancelled)
            {
                return _cancelled.ToArray();
            }
        }
    }

    public void Complete(string jobId, IReadOnlyList<ArtifactRef>? artifacts = null)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            throw new InvalidOperationException($"Unknown job {jobId}");
        }

        var now = DateTimeOffset.UtcNow;
        _jobs[jobId] = job with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = now,
            UpdatedAt = now
        };

        var effectiveArtifacts = artifacts ?? Array.Empty<ArtifactRef>();
        _results[jobId] = new AnalysisResultPackage
        {
            ResultPackageId = $"pkg-{jobId}",
            Status = GeoprocessingWorkflowStatus.Completed,
            Summary = new ResultSummary { Title = "done" },
            Artifacts = effectiveArtifacts,
            Provenance = new ProvenanceRecord
            {
                Sources = Array.Empty<ProvenanceSource>(),
                ProcessDefinitions = Array.Empty<string>(),
                ExecutedAt = now
            }
        };
    }

    public void Fail(string jobId, string reason)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            throw new InvalidOperationException($"Unknown job {jobId}");
        }

        var now = DateTimeOffset.UtcNow;
        _jobs[jobId] = job with
        {
            Status = ExecutionJobStatus.Failed,
            CompletedAt = now,
            UpdatedAt = now,
            ErrorMessage = reason
        };
    }

    public Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey) &&
            _idempotency.TryGetValue(idempotencyKey, out var existingJobId) &&
            _jobs.TryGetValue(existingJobId, out var existing))
        {
            return Task.FromResult(existing);
        }

        lock (_submitted)
        {
            _submitted.Add(plan);
            _submittedMetadata.Add(protocolMetadata);
        }

        var jobId = $"job-{Interlocked.Increment(ref _seq)}";
        var record = OnSubmit?.Invoke(plan, idempotencyKey) ?? new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = plan.PlanId
            }
        };

        _jobs[record.OperationId] = record;
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            _idempotency[idempotencyKey] = record.OperationId;
        }

        return Task.FromResult(record);
    }

    public Task<ExecutionJobRecord> GetJobAsync(string jobId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (NextGetJobFailure is { } failure)
        {
            NextGetJobFailure = null;
            throw failure;
        }

        if (!_jobs.TryGetValue(jobId, out var job))
        {
            throw new KeyNotFoundException(jobId);
        }

        return Task.FromResult(job);
    }

    public Task<AnalysisResultPackage> GetJobResultsAsync(string jobId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (NextGetJobResultsFailure is { } failure)
        {
            NextGetJobResultsFailure = null;
            throw failure;
        }

        if (!_results.TryGetValue(jobId, out var pkg))
        {
            throw new KeyNotFoundException(jobId);
        }

        return Task.FromResult(pkg);
    }

    public Task CancelJobAsync(string jobId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        lock (_cancelled)
        {
            _cancelled.Add(jobId);
        }

        if (NextCancelJobFailure is { } failure)
        {
            NextCancelJobFailure = null;
            throw failure;
        }

        if (_jobs.TryGetValue(jobId, out var job))
        {
            var now = DateTimeOffset.UtcNow;
            _jobs[jobId] = job with
            {
                Status = ExecutionJobStatus.Cancelled,
                CompletedAt = now,
                UpdatedAt = now
            };
        }

        return Task.CompletedTask;
    }
}
