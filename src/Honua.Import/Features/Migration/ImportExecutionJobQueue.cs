// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Migration;

// What a queued import kind stores on its execution job and how a stale progress document reads
// once the job has ended.
internal sealed record ImportExecutionJobKindSpec<TRequest, TProgress>(
    ExecutionJobKind Kind,
    JsonTypeInfo<TRequest> RequestJsonTypeInfo,
    JsonTypeInfo<TProgress> ProgressJsonTypeInfo,
    Func<TProgress, bool> IsTerminal,
    Func<TProgress, ExecutionJobRecord, TProgress> ProjectEndedJob,
    Func<TProgress, TProgress, bool>? AllowTerminalReplacement = null)
    where TRequest : class
    where TProgress : class, IOperationProgress;

internal sealed record ImportExecutionSubmission<TRequest, TProgress>
    where TRequest : class
    where TProgress : class
{
    public required string JobId { get; init; }
    public required TRequest Request { get; init; }
    public required TProgress InitialProgress { get; init; }
    public required string WorkloadName { get; init; }
}

internal sealed class ImportExecutionJobQueue<TRequest, TProgress>
    where TRequest : class
    where TProgress : class, IOperationProgress
{
    internal const string RequestParameter = "honua.import.request";
    internal const string DurableQueueUnavailableMessage = "Queued imports require durable background job storage to be configured.";
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    // The job worker's local queue; the same backend id and target kind every serving-node job uses.
    private const string LocalBackend = "local";
    private const int CancellationAttempts = 3;

    private readonly ImportExecutionJobKindSpec<TRequest, TProgress> _spec;
    private readonly IUniversalProgressStore _universalProgressStore;
    private readonly IExecutionJobStore? _jobStore;
    private readonly IJobQueue? _jobQueue;
    private readonly ILogger _logger;

    public ImportExecutionJobQueue(
        ImportExecutionJobKindSpec<TRequest, TProgress> spec,
        IDistributedProgressStore<TProgress> progressStore,
        IUniversalProgressStore universalProgressStore,
        IExecutionJobStore? jobStore,
        IJobQueue? jobQueue,
        ILogger logger)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _jobStore = jobStore;
        _jobQueue = jobQueue;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _universalProgressStore = universalProgressStore ?? throw new ArgumentNullException(nameof(universalProgressStore));
        ArgumentNullException.ThrowIfNull(progressStore);
        ProgressStore = new EndedJobProgressStore(this, progressStore);
    }

    public IDistributedProgressStore<TProgress> ProgressStore { get; }

    public async Task SubmitAsync(
        ImportExecutionSubmission<TRequest, TProgress> submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var (jobId, request, initialProgress) = (submission.JobId, submission.Request, submission.InitialProgress);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(initialProgress);
        if (_jobStore is null || _jobQueue is null)
        {
            throw new InvalidOperationException(DurableQueueUnavailableMessage);
        }

        // The immutable request and initial status share the execution record's atomic insert.
        // A retry with the same id must never reset progress written by a running worker.
        var created = await _jobStore.TryCreateAsync(CreateRecord(submission), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!created)
        {
            ImportExecutionJobQueueLog.JobAlreadyExists(_logger, jobId);
        }

        await EnqueueAsync(_jobQueue, jobId, CancellationToken.None).ConfigureAwait(false);
    }

    public TRequest? ReadRequest(ExecutionJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Spec.Kind != _spec.Kind || !job.Spec.Parameters.TryGetValue(RequestParameter, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, _spec.RequestJsonTypeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task RequestCancellationAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (_jobStore is null)
        {
            return;
        }

        for (var attempt = 0; attempt < CancellationAttempts; attempt++)
        {
            var job = await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is null || job.Spec.Kind != _spec.Kind || job.Status.IsTerminal() || job.CancellationRequestedAt.HasValue)
            {
                return;
            }

            var claimed = !string.IsNullOrEmpty(job.ClaimedBy);
            var updated = RequestOrCompleteCancellation(job);
            if (await _jobStore.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (!claimed && _jobQueue is not null)
                {
                    await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
        }

        ImportExecutionJobQueueLog.CancellationNotRecorded(_logger, jobId);
        throw new InvalidOperationException("Import cancellation conflicted with concurrent updates. Retry the cancellation.");
    }

    private static ExecutionJobRecord RequestOrCompleteCancellation(ExecutionJobRecord job)
    {
        var now = DateTimeOffset.UtcNow;
        return !string.IsNullOrEmpty(job.ClaimedBy)
            ? job with { CancellationRequestedAt = now, UpdatedAt = now, CurrentPhase = "Cancelling" }
            : job with
            {
                Status = ExecutionJobStatus.Cancelled,
                CancellationRequestedAt = now,
                UpdatedAt = now,
                CompletedAt = now,
                CurrentPhase = "Cancelled"
            };
    }

    // The record is durable, so the import is accepted: the next reconciliation sweep queues a
    // local job whose enqueue was lost.
    private async Task EnqueueAsync(IJobQueue queue, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            await queue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImportExecutionJobQueueLog.EnqueueDeferredToDispatchRepair(_logger, jobId, ex);
        }
    }

    private ExecutionJobRecord CreateRecord(ImportExecutionSubmission<TRequest, TProgress> submission)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = submission.JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            // A large layer or a whole GeoServer catalog can outlast the one-hour default.
            TimeoutPolicy = JobTimeoutPolicy.LongRunning,
            Spec = new ExecutionJobSpec
            {
                Kind = _spec.Kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBackend,
                WorkloadName = submission.WorkloadName,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RequestParameter] = JsonSerializer.Serialize(submission.Request, _spec.RequestJsonTypeInfo),
                    ["honua.import.initialProgress"] = JsonSerializer.Serialize(submission.InitialProgress, _spec.ProgressJsonTypeInfo)
                }
            }
        };
    }

    private async Task<TProgress?> ReadProgressAsync(IDistributedProgressStore<TProgress> inner, string jobId, CancellationToken cancellationToken)
    {
        var progress = await inner.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        if ((progress is not null && _spec.IsTerminal(progress)) || _jobStore is null)
        {
            return progress;
        }

        var job = await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.Spec.Kind != _spec.Kind)
        {
            return progress;
        }

        if (progress is null && job.Spec.Parameters.TryGetValue("honua.import.initialProgress", out var json))
        {
            progress = JsonSerializer.Deserialize(json, _spec.ProgressJsonTypeInfo);
            if (progress is not null && !job.Status.IsTerminal())
            {
                // Recover the progress index after a crash between admission and the first read.
                // Snapshot comparison prevents a late reader from resetting a running worker.
                var seeded = await _universalProgressStore.TryReplaceProgressAsync(
                    new OperationProgressWrite(jobId, progress, Retention), null, cancellationToken).ConfigureAwait(false);
                if (seeded.CurrentProgress is TProgress current)
                {
                    progress = current;
                }
            }
        }

        return progress is not null && job.Status.IsTerminal()
            ? _spec.ProjectEndedJob(progress, job)
            : progress;
    }

    private sealed class EndedJobProgressStore(
        ImportExecutionJobQueue<TRequest, TProgress> owner,
        IDistributedProgressStore<TProgress> inner) : IDistributedProgressStore<TProgress>
    {
        public async Task SetProgressAsync(string jobId, TProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            // A late worker report must not undo cancellation or another terminal result.
            // Compare the whole snapshot so reports in the same phase cannot overwrite a cancel.
            var current = await inner.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
            while (current is null || !owner._spec.IsTerminal(current) ||
                   owner._spec.AllowTerminalReplacement?.Invoke(current, progress) == true)
            {
                var result = await owner._universalProgressStore.TryReplaceProgressAsync(
                    new OperationProgressWrite(jobId, progress, ttl), current, cancellationToken).ConfigureAwait(false);
                if (result.Outcome == ProgressCompareAndSetOutcome.Updated)
                {
                    return;
                }

                current = result.CurrentProgress as TProgress ??
                    await inner.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public Task<TProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
            => owner.ReadProgressAsync(inner, jobId, cancellationToken);

        public Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default)
            => inner.DeleteProgressAsync(jobId, cancellationToken);

        public async Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
        {
            var indexed = await inner.GetActiveJobIdsAsync(cancellationToken).ConfigureAwait(false);
            if (owner._jobStore is null)
            {
                return indexed;
            }

            var jobs = await owner._jobStore.ListActiveAsync(owner._spec.Kind, cancellationToken: cancellationToken).ConfigureAwait(false);
            return indexed.Concat(jobs.Select(job => job.OperationId)).Distinct(StringComparer.Ordinal).ToArray();
        }
    }
}

internal static partial class ImportExecutionJobQueueLog
{
    [LoggerMessage(7600, LogLevel.Warning, "Import job {JobId} was accepted but not queued; dispatch repair will queue it")]
    public static partial void EnqueueDeferredToDispatchRepair(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(7601, LogLevel.Information, "Import job {JobId} already exists; it was queued again")]
    public static partial void JobAlreadyExists(ILogger logger, string jobId);

    [LoggerMessage(7602, LogLevel.Warning, "Cancellation of import job {JobId} was not recorded after repeated conflicts")]
    public static partial void CancellationNotRecorded(ILogger logger, string jobId);
}
