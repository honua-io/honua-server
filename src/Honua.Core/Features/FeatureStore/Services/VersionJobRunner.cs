// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Default <see cref="IVersionJobRunner"/> (#1553). Wraps the synchronous <see cref="IVersionManager"/>
/// reconcile/post in a durable, pollable job: it records the job, runs the operation in the background,
/// persists every lifecycle transition to the <see cref="IVersionJobStore"/>, and emits a telemetry span
/// tagged with version id, kind, policy, conflict/auto-resolved counts, outcome, and duration. The
/// version manager itself holds the (service, version) <see cref="IVersionLock"/> for the critical
/// section, so a job, a competing sync request, and a conflicting resolve are all serialized by the same
/// lock; a contended lock surfaces as <see cref="VersionLockedException"/>, which the runner records as a
/// terminal <see cref="VersionJobStatus.LockContended"/> job. The background work resolves its own DI
/// scope so it is independent of the originating request's lifetime, and the durable store +
/// transactional reconcile make a re-run after a restart idempotent.
/// </summary>
public sealed partial class VersionJobRunner : IVersionJobRunner
{
    private static readonly ActivitySource ActivitySource = new("Honua", "1.0.0");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVersionJobStore _jobStore;
    private readonly ILogger<VersionJobRunner> _logger;
    private readonly IHostApplicationLifetime? _lifetime;

    /// <summary>Initializes the runner.</summary>
    /// <param name="scopeFactory">Factory used to resolve a fresh DI scope for each background job.</param>
    /// <param name="jobStore">Durable job store used to record and poll job state.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="lifetime">
    /// Optional host lifetime (matches the <c>InProcessTemporalCorrectiveJobSink</c> pattern).
    /// When supplied, <see cref="IHostApplicationLifetime.ApplicationStopping"/> is observed by
    /// the detached background job so a graceful shutdown records a terminal state instead of
    /// leaving the job silently abandoned mid-flight. Optional so the runner still works in
    /// hosts/tests that do not register <see cref="IHostApplicationLifetime"/>.
    /// </param>
    public VersionJobRunner(
        IServiceScopeFactory scopeFactory,
        IVersionJobStore jobStore,
        ILogger<VersionJobRunner> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lifetime = lifetime;
    }

    /// <inheritdoc />
    public Task<VersionJob> StartReconcileAsync(
        string service,
        Guid versionId,
        VersionReconcilePolicy policy,
        VersionConflictDetection detection = VersionConflictDetection.ByAttribute,
        CancellationToken cancellationToken = default)
        => StartAsync(service, versionId, VersionJobKind.Reconcile, policy, detection, cancellationToken);

    /// <inheritdoc />
    public Task<VersionJob> StartPostAsync(
        string service,
        Guid versionId,
        CancellationToken cancellationToken = default)
        => StartAsync(
            service, versionId, VersionJobKind.Post, VersionReconcilePolicy.None,
            VersionConflictDetection.ByAttribute, cancellationToken);

    /// <inheritdoc />
    public Task<VersionJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => _jobStore.GetAsync(jobId, cancellationToken);

    private async Task<VersionJob> StartAsync(
        string service,
        Guid versionId,
        VersionJobKind kind,
        VersionReconcilePolicy policy,
        VersionConflictDetection detection,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        var job = new VersionJob(
            JobId: Guid.NewGuid(),
            Service: service,
            VersionId: versionId,
            Kind: kind,
            Status: VersionJobStatus.Pending,
            Policy: policy,
            CreatedAt: DateTimeOffset.UtcNow,
            ConflictDetection: detection);

        await _jobStore.SaveAsync(job, cancellationToken).ConfigureAwait(false);

        // Detach the execution from the request: the background task owns its own DI scope and
        // cancellation so the job completes even after the start response returns.
        _ = Task.Run(() => ExecuteAsync(job), CancellationToken.None);

        return job;
    }

    private async Task ExecuteAsync(VersionJob job)
    {
        // PA-021: Task.Run flows the ambient ExecutionContext by default, so Activity.Current
        // here would still be the HTTP request's activity — which ends when the response
        // returns, long before this detached background job finishes. Capture it as a link
        // instead of letting StartActivity implicitly parent to it. Passing default(ActivityContext)
        // is NOT enough to force a root span: when it is the all-zero/invalid context the
        // ActivitySource falls back to Activity.Current as the implicit parent, so we must null
        // out the ambient activity first so the job span starts a fresh trace of its own.
        var callerContext = Activity.Current?.Context;
        var links = callerContext is { } ctx ? new[] { new ActivityLink(ctx) } : null;
        Activity.Current = null;
        using var activity = ActivitySource.StartActivity(
            $"VersionJob.{job.Kind}", ActivityKind.Internal, default(ActivityContext), links: links);
        activity?.SetTag("honua.version.job_id", job.JobId.ToString());
        activity?.SetTag("honua.version.id", job.VersionId.ToString());
        activity?.SetTag("honua.version.service", job.Service);
        activity?.SetTag("honua.version.job_kind", job.Kind.ToString());
        activity?.SetTag("honua.version.policy", job.Policy.ToString());

        var running = job with { Status = VersionJobStatus.Running, StartedAt = DateTimeOffset.UtcNow };

        // Observed only by the manager call below (not by the Running/terminal-state saves,
        // which must always be attempted best-effort): lets a real host shutdown interrupt a
        // long-running reconcile/post cooperatively instead of the job running unobserved
        // against a process that is already tearing down.
        var shutdownToken = _lifetime?.ApplicationStopping ?? CancellationToken.None;

        // The whole body — including scope creation, service resolution, and the Running-state save —
        // must sit inside the try: this task is detached (fire-and-forget), so an exception escaping
        // here would be unobserved and the job would sit in Pending forever with no log line.
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<IVersionManager>();
            var store = scope.ServiceProvider.GetRequiredService<IVersionJobStore>();

            await store.SaveAsync(running, CancellationToken.None).ConfigureAwait(false);

            VersionJob completed = job.Kind == VersionJobKind.Reconcile
                ? await RunReconcileAsync(manager, running, shutdownToken).ConfigureAwait(false)
                : await RunPostAsync(manager, running, shutdownToken).ConfigureAwait(false);

            activity?.SetTag("honua.version.conflict_count", completed.ConflictCount);
            activity?.SetTag("honua.version.auto_resolved_count", completed.AutoResolvedCount);
            activity?.SetTag("honua.version.applied_changes", completed.AppliedChanges);
            activity?.SetTag("honua.version.outcome", "succeeded");
            activity?.SetStatus(ActivityStatusCode.Ok);
            await store.SaveAsync(completed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // The host is shutting down: record a well-understood terminal state (not a stack
            // trace) so an operator/poller sees a clear reason and knows to resubmit after
            // restart, rather than the job sitting unobserved past process teardown.
            activity?.SetTag("honua.version.outcome", "shutdown");
            activity?.SetStatus(ActivityStatusCode.Error, "Server shutting down");
            Log.JobAbortedForShutdown(_logger, job.Kind.ToString(), job.VersionId, job.JobId);
            var aborted = running with
            {
                Status = VersionJobStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "The reconcile/post job was aborted because the server is shutting down. Resubmit after restart.",
            };
            await SaveTerminalStateAsync(aborted).ConfigureAwait(false);
        }
        catch (VersionLockedException)
        {
            // Another reconcile/post for this version is already in flight; record the contention as a
            // terminal outcome rather than blocking behind it.
            activity?.SetTag("honua.version.outcome", "lock_contended");
            var contended = running with
            {
                Status = VersionJobStatus.LockContended,
                StartedAt = null,
                CompletedAt = DateTimeOffset.UtcNow,
            };
            await SaveTerminalStateAsync(contended).ConfigureAwait(false);
            Log.LockContended(_logger, job.Kind.ToString(), job.VersionId, job.JobId, null);
        }
        catch (Exception ex)
        {
            // Surface a sanitized message only; provider internals never leak through the job record.
            activity?.SetTag("honua.version.outcome", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Log.JobFailed(_logger, job.Kind.ToString(), job.VersionId, job.JobId, ex);
            var failed = running with
            {
                Status = VersionJobStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "The reconcile/post job failed. See server logs for details.",
            };
            await SaveTerminalStateAsync(failed).ConfigureAwait(false);
        }
    }

    // Best-effort: the terminal transition is recorded through the singleton job store (the DI scope
    // may already be gone when the failure was in the prologue), and a store outage while saving must
    // not become a second unobserved exception on this detached task.
    private async Task SaveTerminalStateAsync(VersionJob job)
    {
        try
        {
            await _jobStore.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.TerminalSaveFailed(_logger, job.Kind.ToString(), job.VersionId, job.JobId, ex);
        }
    }

    private static async Task<VersionJob> RunReconcileAsync(
        IVersionManager manager,
        VersionJob job,
        CancellationToken cancellationToken)
    {
        var result = await manager.ReconcileAsync(job.VersionId, job.Policy, job.ConflictDetection, cancellationToken).ConfigureAwait(false);
        var conflictCount = result.Conflicts.IsDefaultOrEmpty ? 0 : result.Conflicts.Length;
        return job with
        {
            Status = VersionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow,
            ConflictCount = conflictCount,
            AutoResolvedCount = result.AutoResolvedCount,
            CanPost = result.CanPost,
            ServerGeneration = result.NewCommonAncestorGeneration,
        };
    }

    private static async Task<VersionJob> RunPostAsync(
        IVersionManager manager,
        VersionJob job,
        CancellationToken cancellationToken)
    {
        var result = await manager.PostAsync(job.VersionId, cancellationToken).ConfigureAwait(false);
        return job with
        {
            Status = VersionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow,
            AppliedChanges = result.AppliedChanges,
            ServerGeneration = result.ServerGeneration,
            BlockedByConflicts = result.BlockedByConflicts,
            CanPost = !result.BlockedByConflicts,
        };
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7100,
            Level = LogLevel.Warning,
            Message = "Version {JobKind} job for version {VersionId} (job {JobId}) skipped: version lock contended.")]
        public static partial void LockContended(ILogger logger, string jobKind, Guid versionId, Guid jobId, Exception? exception);

        [LoggerMessage(
            EventId = 7101,
            Level = LogLevel.Error,
            Message = "Version {JobKind} job for version {VersionId} (job {JobId}) failed.")]
        public static partial void JobFailed(ILogger logger, string jobKind, Guid versionId, Guid jobId, Exception exception);

        [LoggerMessage(
            EventId = 7102,
            Level = LogLevel.Error,
            Message = "Failed to persist terminal state for version {JobKind} job {JobId} (version {VersionId}); polling clients may see a stale status.")]
        public static partial void TerminalSaveFailed(ILogger logger, string jobKind, Guid versionId, Guid jobId, Exception exception);

        [LoggerMessage(
            EventId = 7103,
            Level = LogLevel.Warning,
            Message = "Version {JobKind} job for version {VersionId} (job {JobId}) aborted: server is shutting down.")]
        public static partial void JobAbortedForShutdown(ILogger logger, string jobKind, Guid versionId, Guid jobId);
    }
}
