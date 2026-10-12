// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import.FileImport;
using Honua.Import.FileImport.Durable;

namespace Honua.Server.Features.Import;

/// <summary>
/// The host's <see cref="IImportJobService"/> (honua-server#5916). Routes a background file import
/// by the composition-time <see cref="BackgroundImportRunnerDecision"/>:
/// <list type="bullet">
/// <item>durable runner configured: the import becomes a <see cref="ExecutionJobKind.FileImport"/>
/// execution job carrying the staged object-store reference, enqueued on the local queue (drained by
/// the server's <c>JobExecutionService</c>) or submitted to the remote batch backend (AWS Batch,
/// whose worker-only profile runs <see cref="FileImportJobExecutor"/>);</item>
/// <item>long-lived host without a runner: the in-process <c>UniversalImportJobService</c>;</item>
/// <item>Lambda host without a runner: refused (the endpoint answers the typed 503 first).</item>
/// </list>
/// Reads always go through the import job document; a durable job whose execution record ended
/// without the worker writing a terminal document (crash, cancel, timeout, rejected submission)
/// is projected onto it, so a job never reads as <c>processing</c> forever.
/// </summary>
internal sealed partial class DurableImportJobService : IImportJobService, IDisposable
{
    internal const string CancelledPhase = "Cancelled by operator";
    private const string SafeImportFailureMessage = "Import failed.";
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(1);

    /// <summary>Ephemeral (scratch) storage headroom per staged byte for the batch job-definition tier.</summary>
    private const int EphemeralBytesMultiplier = 3;
    private const long BytesPerGib = 1024L * 1024 * 1024;

    private readonly IImportJobService _inProcess;
    private readonly BackgroundImportRunnerDecision _decision;
    private readonly IExecutionJobStore? _jobStore;
    private readonly IJobQueue? _jobQueue;
    private readonly IReadOnlyList<IBatchComputeBackend> _backends;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string? _instanceId;
    private readonly ILogger<DurableImportJobService> _logger;

    public DurableImportJobService(
        IImportJobService inProcess,
        BackgroundImportRunnerDecision decision,
        IUniversalProgressStore progressStore,
        IServiceScopeFactory scopeFactory,
        IEnumerable<IBatchComputeBackend> backends,
        ILogger<DurableImportJobService> logger,
        IExecutionJobStore? jobStore = null,
        IJobQueue? jobQueue = null,
        IInstanceIdentity? instanceIdentity = null)
    {
        _inProcess = inProcess ?? throw new ArgumentNullException(nameof(inProcess));
        _decision = decision ?? throw new ArgumentNullException(nameof(decision));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _backends = (backends ?? throw new ArgumentNullException(nameof(backends))).ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jobStore = jobStore;
        _jobQueue = jobQueue;
        _instanceId = instanceIdentity?.InstanceId;

        if (decision.IsDurable && jobStore is null)
        {
            throw new InvalidOperationException("A durable background-import runner requires the execution job store.");
        }
    }

    public async Task<string> QueueImportAsync(ImportRequest request, long fileSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (_decision.Mode)
        {
            case BackgroundImportRunnerMode.InProcess:
                return await _inProcess.QueueImportAsync(request, fileSize, cancellationToken).ConfigureAwait(false);
            case BackgroundImportRunnerMode.Unavailable:
                throw new InvalidOperationException(ImportEndpoints.BackgroundImportUnavailableMessage);
        }

        var jobId = Guid.NewGuid().ToString("N");
        var queued = FileImportJobSubmission.CreateQueuedProgress(
            _scopeFactory, request, fileSize, jobId, _decision.Runner, _instanceId);
        var job = BuildJobRecord(jobId, request, fileSize, _decision, DateTimeOffset.UtcNow);

        if (!await _jobStore!.TryCreateAsync(job, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to create file-import execution job '{jobId}'.");
        }

        // The store stamps the created record's version; the remote submission's CAS writes must
        // start from the persisted record, not the unversioned draft.
        job = await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false) ?? job;

        try
        {
            await _progressStore.SetProgressAsync(jobId, queued, ProgressRetention, cancellationToken).ConfigureAwait(false);
            if (_decision.Mode == BackgroundImportRunnerMode.DurableLocal)
            {
                if (_jobQueue is null)
                {
                    throw new InvalidOperationException("The local durable job queue is not composed.");
                }

                await _jobQueue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind)
                    ?? throw new InvalidOperationException(
                        $"No batch compute backend registered for '{job.Spec.Backend}' ({job.Spec.TargetKind}).");
                await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
                    job, backend, _jobStore, _progressStore, ProgressRetention, _logger, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                _jobStore,
                jobId,
                failureMessage: "File-import submission failed.",
                logger: _logger,
                cancellationToken: cleanup.Token).ConfigureAwait(false);
            await TryWriteProgressAsync(jobId, queued with
            {
                Status = ImportStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = SafeImportFailureMessage,
                CurrentPhase = "Import failed",
            }).ConfigureAwait(false);
            throw;
        }

        Log.JobSubmitted(_logger, jobId, request.TableName, _decision.Runner, job.Spec.Backend);
        return jobId;
    }

    public async Task<ImportProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var progress = await _inProcess.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (progress is null || IsTerminal(progress.Status) || _jobStore is null)
        {
            return progress;
        }

        var job = await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job is null ? progress : ProjectExecutionOutcome(progress, job);
    }

    public async Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = _jobStore is null || string.IsNullOrWhiteSpace(jobId)
            ? null
            : await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.Spec.Kind != ExecutionJobKind.FileImport)
        {
            return await _inProcess.CancelJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        if (ExecutionJobCancellationHelper.IsTerminal(job.Status))
        {
            return false;
        }

        bool requested;
        if (string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            var outcome = await ExecutionJobCancellationHelper.TryApplyAsync(
                _jobStore!, jobId, job, CancelledPhase, cancellationToken: cancellationToken).ConfigureAwait(false);
            requested = outcome.State is ExecutionJobCancellationState.Cancelled or ExecutionJobCancellationState.CancellationRequested;
            job = outcome.Job ?? job;
        }
        else
        {
            var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind);
            if (backend is null)
            {
                return false;
            }

            var observation = await backend.CancelAsync(job, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var cancelling = job with
            {
                CancellationRequestedAt = now,
                UpdatedAt = now,
                Status = observation.Status,
                CompletedAt = ExecutionJobCancellationHelper.IsTerminal(observation.Status) ? now : job.CompletedAt,
                CurrentPhase = observation.Message ?? "Cancellation requested",
            };
            requested = await _jobStore!.TrySetAsync(cancelling, cancellationToken: cancellationToken).ConfigureAwait(false);
            job = cancelling;
        }

        if (requested)
        {
            var progress = await _inProcess.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (progress is not null && !IsTerminal(progress.Status))
            {
                await TryWriteProgressAsync(jobId, job.Status == ExecutionJobStatus.Cancelled
                    ? progress with
                    {
                        Status = ImportStatus.Cancelled,
                        CompletedAt = job.CompletedAt ?? DateTimeOffset.UtcNow,
                        CurrentPhase = CancelledPhase,
                    }
                    : progress with { CurrentPhase = "Cancellation requested" }).ConfigureAwait(false);
            }

            Log.CancellationRequested(_logger, jobId, job.Spec.Backend);
        }

        return requested;
    }

    public Task<IReadOnlyList<ImportProgress>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
        => _inProcess.GetActiveJobsAsync(cancellationToken);

    public void Dispose() => (_inProcess as IDisposable)?.Dispose();

    /// <summary>Builds the durable record for a staged import.</summary>
    internal static ExecutionJobRecord BuildJobRecord(
        string jobId,
        ImportRequest request,
        long fileSize,
        BackgroundImportRunnerDecision decision,
        DateTimeOffset now)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        ExecutionJobSpec spec;
        if (decision.Mode == BackgroundImportRunnerMode.DurableRemote)
        {
            var workload = decision.Workload!;
            foreach (var pair in workload.Parameters)
            {
                parameters[pair.Key] = pair.Value;
            }

            // Scratch-storage hint for the AWS Batch job-definition tier (the worker downloads the
            // staged object before streaming it). Small files stay on the default tier.
            var ephemeralGib = (int)Math.Min(int.MaxValue, (fileSize * EphemeralBytesMultiplier / BytesPerGib) + 1);
            if (ephemeralGib > 20)
            {
                parameters["batch.ephemeral_gib"] = ephemeralGib.ToString(CultureInfo.InvariantCulture);
            }

            AddImportParameters(parameters, request, fileSize, decision.Runner);
            spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.FileImport,
                TargetKind = workload.TargetKind,
                Backend = workload.Backend,
                WorkloadId = workload.WorkloadId,
                WorkloadName = workload.WorkloadName,
                Artifact = workload.ArtifactReference,
                RuntimeProfile = workload.RuntimeProfile,
                ContractVersion = workload.ContractVersion,
                Parameters = parameters,
            };
        }
        else
        {
            AddImportParameters(parameters, request, fileSize, decision.Runner);
            spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.FileImport,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = BackgroundImportRunnerDecision.LocalWorkloadNamePrefix + request.TableName,
                Parameters = parameters,
            };
        }

        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            // Carries the accepting request's lineage and trace context across the queue boundary
            // so the worker's execution span links back to the upload (#5474).
            Audit = ControlPlaneTelemetry.WithSubmissionTraceContext(new OperationAuditInfo
            {
                OperationInstanceId = request.OperationInstanceId,
                CorrelationId = request.CorrelationId,
                AuditId = request.AuditId,
                ProposalId = request.ProposalId,
            }, System.Diagnostics.Activity.Current),
            // A large import can outlast the one-hour default; the batch backend's own timeout
            // and the heartbeat reaper still bound a stuck attempt.
            TimeoutPolicy = JobTimeoutPolicy.LongRunning,
            Spec = spec,
        };
    }

    /// <summary>
    /// Projects a terminal execution record onto a non-terminal import document: the worker died,
    /// the job was cancelled or timed out, or the submission was rejected.
    /// </summary>
    internal static ImportProgress ProjectExecutionOutcome(ImportProgress progress, ExecutionJobRecord job)
    {
        if (job.Spec.Kind != ExecutionJobKind.FileImport || IsTerminal(progress.Status))
        {
            return progress;
        }

        return job.Status switch
        {
            ExecutionJobStatus.Failed => progress with
            {
                Status = ImportStatus.Failed,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                ErrorMessage = SafeImportFailureMessage,
                CurrentPhase = job.CurrentPhase ?? "Import failed",
            },
            ExecutionJobStatus.Cancelled => progress with
            {
                Status = ImportStatus.Cancelled,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                CurrentPhase = job.CurrentPhase ?? CancelledPhase,
            },
            _ => progress,
        };
    }

    private static void AddImportParameters(
        Dictionary<string, string> parameters,
        ImportRequest request,
        long fileSize,
        string runner)
    {
        foreach (var pair in FileImportJobParameters.Build(request, fileSize))
        {
            parameters[pair.Key] = pair.Value;
        }

        parameters[FileImportJobParameters.Runner] = runner;
    }

    private static bool IsTerminal(ImportStatus status)
        => status is ImportStatus.Completed or ImportStatus.Failed or ImportStatus.Cancelled;

    private async Task TryWriteProgressAsync(string jobId, ImportProgress progress)
    {
        try
        {
            await _progressStore.SetProgressAsync(jobId, progress, ProgressRetention, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad: best-effort document update; the durable record stays authoritative
            // and GetProgressAsync projects it.
            Log.ProgressWriteFailed(_logger, jobId, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7487, LogLevel.Information,
            "File-import job {JobId} submitted table={TableName} runner={Runner} backend={Backend}")]
        public static partial void JobSubmitted(ILogger logger, string jobId, string tableName, string runner, string backend);

        [LoggerMessage(7488, LogLevel.Information, "File-import job {JobId} cancellation requested on backend {Backend}")]
        public static partial void CancellationRequested(ILogger logger, string jobId, string backend);

        [LoggerMessage(7489, LogLevel.Warning, "File-import job {JobId} could not update its job document")]
        public static partial void ProgressWriteFailed(ILogger logger, string jobId, Exception exception);
    }
}
