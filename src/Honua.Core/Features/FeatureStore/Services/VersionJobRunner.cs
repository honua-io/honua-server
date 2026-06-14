// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Initializes the runner.</summary>
    /// <param name="scopeFactory">Factory used to resolve a fresh DI scope for each background job.</param>
    /// <param name="jobStore">Durable job store used to record and poll job state.</param>
    /// <param name="logger">Logger.</param>
    public VersionJobRunner(
        IServiceScopeFactory scopeFactory,
        IVersionJobStore jobStore,
        ILogger<VersionJobRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<VersionJob> StartReconcileAsync(
        string service,
        Guid versionId,
        VersionReconcilePolicy policy,
        CancellationToken cancellationToken = default)
        => StartAsync(service, versionId, VersionJobKind.Reconcile, policy, cancellationToken);

    /// <inheritdoc />
    public Task<VersionJob> StartPostAsync(
        string service,
        Guid versionId,
        CancellationToken cancellationToken = default)
        => StartAsync(service, versionId, VersionJobKind.Post, VersionReconcilePolicy.None, cancellationToken);

    /// <inheritdoc />
    public Task<VersionJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => _jobStore.GetAsync(jobId, cancellationToken);

    private async Task<VersionJob> StartAsync(
        string service,
        Guid versionId,
        VersionJobKind kind,
        VersionReconcilePolicy policy,
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
            CreatedAt: DateTimeOffset.UtcNow);

        await _jobStore.SaveAsync(job, cancellationToken).ConfigureAwait(false);

        // Detach the execution from the request: the background task owns its own DI scope and
        // cancellation so the job completes even after the start response returns.
        _ = Task.Run(() => ExecuteAsync(job), CancellationToken.None);

        return job;
    }

    private async Task ExecuteAsync(VersionJob job)
    {
        using var activity = ActivitySource.StartActivity($"VersionJob.{job.Kind}", ActivityKind.Internal);
        activity?.SetTag("honua.version.job_id", job.JobId.ToString());
        activity?.SetTag("honua.version.id", job.VersionId.ToString());
        activity?.SetTag("honua.version.service", job.Service);
        activity?.SetTag("honua.version.job_kind", job.Kind.ToString());
        activity?.SetTag("honua.version.policy", job.Policy.ToString());

        await using var scope = _scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IVersionManager>();
        var store = scope.ServiceProvider.GetRequiredService<IVersionJobStore>();

        var running = job with { Status = VersionJobStatus.Running, StartedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(running, CancellationToken.None).ConfigureAwait(false);

        try
        {
            VersionJob completed = job.Kind == VersionJobKind.Reconcile
                ? await RunReconcileAsync(manager, running, CancellationToken.None).ConfigureAwait(false)
                : await RunPostAsync(manager, running, CancellationToken.None).ConfigureAwait(false);

            activity?.SetTag("honua.version.conflict_count", completed.ConflictCount);
            activity?.SetTag("honua.version.auto_resolved_count", completed.AutoResolvedCount);
            activity?.SetTag("honua.version.applied_changes", completed.AppliedChanges);
            activity?.SetTag("honua.version.outcome", "succeeded");
            activity?.SetStatus(ActivityStatusCode.Ok);
            await store.SaveAsync(completed, CancellationToken.None).ConfigureAwait(false);
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
            await store.SaveAsync(contended, CancellationToken.None).ConfigureAwait(false);
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
            await store.SaveAsync(failed, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<VersionJob> RunReconcileAsync(
        IVersionManager manager,
        VersionJob job,
        CancellationToken cancellationToken)
    {
        var result = await manager.ReconcileAsync(job.VersionId, job.Policy, cancellationToken).ConfigureAwait(false);
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
    }
}
