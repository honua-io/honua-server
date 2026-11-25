// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.FeatureStore.Services;

internal sealed record VersionJobRunnerOptions
{
    public required IServiceScopeFactory ScopeFactory { get; init; }

    public required IVersionJobStore JobStore { get; init; }

    public required ILogger<VersionJobRunner> Logger { get; init; }

    public IHostApplicationLifetime? Lifetime { get; init; }

    public int MaxTerminalSaveAttempts { get; init; } = 8;

    public TimeSpan InitialTerminalSaveRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxTerminalSaveRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed partial class VersionJobRunner : IVersionJobRunner
{
    private static readonly ActivitySource ActivitySource = new("Honua", "1.0.0");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVersionJobStore _jobStore;
    private readonly ILogger<VersionJobRunner> _logger;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly int _maxTerminalSaveAttempts;
    private readonly TimeSpan _initialTerminalSaveRetryDelay;
    private readonly TimeSpan _maxTerminalSaveRetryDelay;
    private readonly ConcurrentDictionary<Guid, Task> _inFlightJobs = new();

    public VersionJobRunner(
        IServiceScopeFactory scopeFactory,
        IVersionJobStore jobStore,
        ILogger<VersionJobRunner> logger,
        IHostApplicationLifetime? lifetime = null)
        : this(new VersionJobRunnerOptions
        {
            ScopeFactory = scopeFactory,
            JobStore = jobStore,
            Logger = logger,
            Lifetime = lifetime,
        })
    {
    }

    internal VersionJobRunner(VersionJobRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTerminalSaveAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.InitialTerminalSaveRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaxTerminalSaveRetryDelay,
            options.InitialTerminalSaveRetryDelay);

        ArgumentNullException.ThrowIfNull(options.ScopeFactory);
        ArgumentNullException.ThrowIfNull(options.JobStore);
        ArgumentNullException.ThrowIfNull(options.Logger);

        _scopeFactory = options.ScopeFactory;
        _jobStore = options.JobStore;
        _logger = options.Logger;
        _lifetime = options.Lifetime;
        _maxTerminalSaveAttempts = options.MaxTerminalSaveAttempts;
        _initialTerminalSaveRetryDelay = options.InitialTerminalSaveRetryDelay;
        _maxTerminalSaveRetryDelay = options.MaxTerminalSaveRetryDelay;
    }

    internal async Task WaitForInFlightJobsAsync(CancellationToken cancellationToken = default)
    {
        var pending = _inFlightJobs.Values.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<VersionJob> StartReconcileAsync(
        string service,
        Guid versionId,
        VersionReconcilePolicy policy,
        VersionConflictDetection detection = VersionConflictDetection.ByAttribute,
        CancellationToken cancellationToken = default)
        => StartAsync(service, versionId, VersionJobKind.Reconcile, policy, detection, cancellationToken);

    public Task<VersionJob> StartPostAsync(
        string service,
        Guid versionId,
        CancellationToken cancellationToken = default)
        => StartAsync(
            service, versionId, VersionJobKind.Post, VersionReconcilePolicy.None,
            VersionConflictDetection.ByAttribute, cancellationToken);

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
        TrackBackgroundExecution(job);

        return job;
    }

    private void TrackBackgroundExecution(VersionJob job)
    {
        // Detach the execution from the request: the background task owns its own DI scope and
        // cancellation so the job completes even after the start response returns, while still
        // being tracked so callers or graceful shutdown can observe pending work.
        var trackedTask = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(job).ConfigureAwait(false);
            }
            finally
            {
                _inFlightJobs.TryRemove(job.JobId, out _);
            }
        }, CancellationToken.None);

        if (!trackedTask.IsCompleted)
        {
            _inFlightJobs[job.JobId] = trackedTask;
            if (trackedTask.IsCompleted)
            {
                _inFlightJobs.TryRemove(job.JobId, out _);
            }
        }
    }

    private async Task ExecuteAsync(VersionJob job)
    {
        // Task.Run flows the ambient ExecutionContext by default, so Activity.Current
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
            if (completed.Status == VersionJobStatus.Succeeded)
            {
                await InvalidateQueryResponsesBestEffortAsync(scope.ServiceProvider).ConfigureAwait(false);
            }
            await SaveTerminalStateAsync(completed).ConfigureAwait(false);
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
        // Intentional broad catch: this is the top-level boundary for a detached background job.
        // Any unexpected failure must be recorded as a terminal job state rather than becoming an
        // unobserved task exception; only a sanitized message is surfaced, provider internals
        // never leak through the job record.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

    private async Task InvalidateQueryResponsesBestEffortAsync(IServiceProvider services)
    {
        try
        {
            var invalidator = services.GetService<IQueryResponseCacheInvalidator>();
            if (invalidator is null)
            {
                return;
            }

            await invalidator.InvalidateQueryResponsesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.QueryResponseInvalidationFailed(_logger, ex);
        }
    }

    private async Task SaveTerminalStateAsync(VersionJob job)
    {
        var shutdownToken = _lifetime?.ApplicationStopping ?? CancellationToken.None;
        for (var attempt = 1; attempt <= _maxTerminalSaveAttempts; attempt++)
        {
            try
            {
                await _jobStore.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (IsNonTransientSaveFailure(ex) || attempt >= _maxTerminalSaveAttempts)
                {
                    Log.TerminalSaveAbandoned(_logger, job.Kind.ToString(), job.VersionId, job.JobId, attempt, ex);
                    return;
                }

                Log.TerminalSaveFailed(_logger, job.Kind.ToString(), job.VersionId, job.JobId, ex);
            }

            // Retain the operation's original outcome through a transient outage without replaying it.
            var delay = ComputeTerminalSaveRetryDelay(attempt);
            try
            {
                await Task.Delay(delay, shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static bool IsNonTransientSaveFailure(Exception exception)
        => exception is ArgumentException or NotSupportedException or FormatException;

    private TimeSpan ComputeTerminalSaveRetryDelay(int attempt)
    {
        var exponent = Math.Min(attempt - 1, 16);
        var scaledTicks = _initialTerminalSaveRetryDelay.Ticks * (1L << exponent);
        var baseTicks = scaledTicks > 0 && scaledTicks < _maxTerminalSaveRetryDelay.Ticks
            ? scaledTicks
            : _maxTerminalSaveRetryDelay.Ticks;
        var jitterFactor = 1.0 + (Random.Shared.NextDouble() * 0.25);
        var jitteredTicks = (long)Math.Min(baseTicks * jitterFactor, _maxTerminalSaveRetryDelay.Ticks);
        return TimeSpan.FromTicks(Math.Max(1L, jitteredTicks));
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

        [LoggerMessage(
            EventId = 7104,
            Level = LogLevel.Warning,
            Message = "Query response cache invalidation failed after a version job completed.")]
        public static partial void QueryResponseInvalidationFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 7105,
            Level = LogLevel.Error,
            Message = "Abandoned persisting terminal state for version {JobKind} job {JobId} (version {VersionId}) after {Attempts} attempt(s).")]
        public static partial void TerminalSaveAbandoned(ILogger logger, string jobKind, Guid versionId, Guid jobId, int attempts, Exception exception);
    }
}
