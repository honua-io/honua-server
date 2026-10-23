// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import.FileImport;
using Honua.Import.FileImport.Durable;
using Honua.Server.Features.AgentWorkspace;

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
/// Reads use the import job document, falling back to the original durable spec if the document
/// is missing. Terminal execution records restore their persisted output receipt when available,
/// without inferring table details or dispatching another job.
/// </summary>
internal sealed partial class DurableImportJobService : IImportJobService, IDisposable
{
    internal const string CancelledPhase = "Cancelled by operator";
    private const string SafeImportFailureMessage = "Import failed.";
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(1);

    /// <summary>Ephemeral (scratch) storage headroom per staged byte for the batch job-definition tier.</summary>
    private const int EphemeralBytesMultiplier = 3;
    private const long BytesPerGib = 1024L * 1024 * 1024;
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);

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

    public Task<string> QueueImportAsync(ImportRequest request, long fileSize, CancellationToken cancellationToken = default)
        => QueueCoreAsync(request, fileSize, null, null, null, cancellationToken);

    // Only the server task coordinator calls this after actual-caller authorization.
    // The lease policy must independently verify the retained task/preparation and
    // current source/destination policy. This path has no default HTTP registration.
    internal Task<string> QueueDatasetImportAsync(DatasetImportTask task, DatasetImportPreparedAdmission prepared,
        ImportStagedSourceBinding source, ClaimsPrincipal caller, ClaimsPrincipal executor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(executor);
        var intent = DatasetImportSubmissionIntent.Create(task, prepared, source);
        return QueueCoreAsync(intent.Request, source.Bytes, intent, caller, executor, cancellationToken);
    }

    private async Task<string> QueueCoreAsync(ImportRequest request, long fileSize,
        DatasetImportSubmissionIntent? intent, ClaimsPrincipal? caller, ClaimsPrincipal? executor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Acceptance?.Validate();
        request.StagedSource?.Validate();
        if (request.StagedSource?.Transfer is not null && intent is null)
            throw new UnauthorizedAccessException("Task-bound transfers require the canonical prepared import authority.");
        if ((request.Acceptance is not null || request.StagedSource is not null) && !_decision.IsDurable)
        {
            throw new InvalidOperationException("A bound import acceptance or staged source requires a durable job runner.");
        }

        switch (_decision.Mode)
        {
            case BackgroundImportRunnerMode.InProcess:
                return await _inProcess.QueueImportAsync(request, fileSize, cancellationToken).ConfigureAwait(false);
            case BackgroundImportRunnerMode.Unavailable:
                throw new InvalidOperationException(ImportEndpoints.BackgroundImportUnavailableMessage);
        }

        var jobId = request.Acceptance?.OperationId ?? Guid.NewGuid().ToString("N");
        var (queued, limits) = FileImportJobSubmission.CreateQueuedProgress(
            _scopeFactory, request, fileSize, jobId, _decision.Runner, _instanceId);
        if (request.Acceptance is { } acceptance)
        {
            var existing = await _jobStore!.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireSameAcceptance(existing, request, fileSize);
                await RequireSameTaskAuthorityAsync(existing, intent, caller, cancellationToken).ConfigureAwait(false);
                return existing.OperationId;
            }

            // Upload sessions admit for at most one day, while execution receipts
            // remain for seven. Expired unknown intents must never recreate a job
            // whose receipt might already have been retired.
            var now = DateTimeOffset.UtcNow;
            if (acceptance.AcceptBefore <= now || acceptance.AcceptBefore > now.AddDays(1) ||
                string.IsNullOrWhiteSpace(request.Ownership?.OwnerActorId))
            {
                throw new InvalidOperationException("The import acceptance binding is expired, unowned or outside its admission window.");
            }
        }

        await EnsureAdmittedAsync(limits, cancellationToken).ConfigureAwait(false);
        var job = BuildJobRecord(jobId, request, fileSize, _decision, DateTimeOffset.UtcNow);

        if (intent is not null)
        {
            job = job with
            {
                Audit = job.Audit with
                {
                    RequestedBy = intent.ExecutorContext.OwnerActorId,
                    TenantId = intent.ExecutorContext.TenantId,
                    SubmitterSecurityContext = intent.ExecutorContext
                }
            };
            await using var authorityScope = _scopeFactory.CreateAsyncScope();
            await intent.RequireCurrentCallerAsync(authorityScope.ServiceProvider, caller!, cancellationToken).ConfigureAwait(false);
            var authority = authorityScope.ServiceProvider.GetRequiredService<IJobExecutionLeaseAuthority>();
            var deadline = DateTimeOffset.UtcNow.Add(job.TimeoutPolicy!.MaxDuration);
            if (request.StagedSource!.ExpiresAt is { } sourceDeadline && sourceDeadline < deadline)
                deadline = sourceDeadline;
            var lease = await authority.IssueAsync(new JobExecutionLeaseIssuanceRequest
            {
                PreparedJob = job,
                Executor = executor!,
                Admission = intent.Admission,
                RequestedExpiresAt = deadline
            }, cancellationToken).ConfigureAwait(false);
            await intent.RequireCurrentCallerAsync(authorityScope.ServiceProvider, caller!, cancellationToken).ConfigureAwait(false);
            job = job with
            {
                Audit = job.Audit with { SubmitterSecurityContext = intent.ExecutorContext with { ExecutionLease = lease } }
            };
        }

        if (request.Acceptance is { } bound && bound.AcceptBefore <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The import acceptance deadline has passed.");
        }

        if (!await _jobStore!.TryCreateAsync(job, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (request.Acceptance is not null &&
                await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false) is { } accepted)
            {
                RequireSameAcceptance(accepted, request, fileSize);
                await RequireSameTaskAuthorityAsync(accepted, intent, caller, cancellationToken).ConfigureAwait(false);
                return accepted.OperationId;
            }

            throw new InvalidOperationException($"Failed to create file-import execution job '{jobId}'.");
        }

        // Once the record exists the dispatch must finish (or be compensated) whatever happens to
        // the request: a caller that disconnects now must not strand a Queued record nobody enqueued.
        using var dispatch = new CancellationTokenSource(DispatchTimeout);
        var dispatchToken = dispatch.Token;
        try
        {
            // The store stamps the created record's version; the remote submission's CAS writes must
            // start from the persisted record, not the unversioned draft.
            job = await _jobStore.GetAsync(jobId, dispatchToken).ConfigureAwait(false) ?? job;
            await _progressStore.SetProgressAsync(jobId, queued, ProgressRetention, dispatchToken).ConfigureAwait(false);
            if (_decision.Mode == BackgroundImportRunnerMode.DurableLocal)
            {
                if (_jobQueue is null)
                {
                    throw new InvalidOperationException("The local durable job queue is not composed.");
                }

                await _jobQueue.EnqueueAsync(jobId, cancellationToken: dispatchToken).ConfigureAwait(false);
            }
            else
            {
                var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind)
                    ?? throw new InvalidOperationException(
                        $"No batch compute backend registered for '{job.Spec.Backend}' ({job.Spec.TargetKind}).");
                await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
                    job, backend, _jobStore, _progressStore, ProgressRetention, _logger, dispatchToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad: any dispatch fault, timeout included, fails the record and the
            // document so the job never reads as queued forever.
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

    private async Task RequireSameTaskAuthorityAsync(ExecutionJobRecord existing, DatasetImportSubmissionIntent? intent,
        ClaimsPrincipal? caller, CancellationToken cancellationToken)
    {
        if (intent is null) return;
        if (existing.Audit.SubmitterSecurityContext is not { ExecutionLease: not null } original ||
            existing.Audit.RequestedBy != intent.ExecutorContext.OwnerActorId || existing.Audit.TenantId != intent.ExecutorContext.TenantId ||
            JobExecutionLeaseIntentFingerprint.ComputeContext(original) != JobExecutionLeaseIntentFingerprint.ComputeContext(intent.ExecutorContext))
            throw new ImportAcceptanceConflictException();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var observation = await scope.ServiceProvider.GetRequiredService<IJobExecutionLeaseAuthority>()
            .ResolveForObservationAsync(existing.OperationId, caller!, JobExecutionLeaseObservationKind.Status, cancellationToken).ConfigureAwait(false);
        if (observation.JobId != existing.OperationId || observation.Admission != intent.Admission ||
            observation.LeaseId != original.ExecutionLease.LeaseId ||
            observation.IntentFingerprint != JobExecutionLeaseIntentFingerprint.Compute(existing) ||
            observation.ExecutorActorId != intent.ExecutorContext.OwnerActorId || observation.TenantId != intent.ExecutorContext.TenantId)
            throw new ImportAcceptanceConflictException();
    }

    private static void RequireSameAcceptance(ExecutionJobRecord existing, ImportRequest request, long fileSize)
    {
        // A retry keeps the original submission trace lineage rather than replacing it
        // with the retry request's Activity baggage. Explicit request lineage must
        // still match the accepted record.
        var expected = FileImportJobParameters.Build(request with
        {
            CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                ? existing.Spec.Parameters.GetValueOrDefault(FileImportJobParameters.CorrelationId)
                : request.CorrelationId
        }, fileSize);
        // Runtime/backend resolution can append non-import parameters. Compare
        // every immutable import input, including owner, tenant, source and audit
        // lineage, and reject any additional import inputs on the original record.
        var actual = existing.Spec.Parameters.Where(static pair =>
            pair.Key.StartsWith("import.", StringComparison.Ordinal) && pair.Key != FileImportJobParameters.Runner).ToArray();
        if (existing.Spec.Kind != ExecutionJobKind.FileImport || existing.OperationId != request.Acceptance!.OperationId ||
            existing.AcceptanceDeadline != request.Acceptance.AcceptBefore ||
            actual.Length != expected.Count || actual.Any(pair => !expected.TryGetValue(pair.Key, out var value) || value != pair.Value))
        {
            throw new ImportAcceptanceConflictException();
        }
    }

    public async Task<ImportProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var progress = await _inProcess.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (_jobStore is null || progress is not null && IsTerminal(progress.Status))
        {
            return progress;
        }

        var job = await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.Spec.Kind != ExecutionJobKind.FileImport)
        {
            return progress;
        }

        // The progress document has shorter retention than the durable job and can also be
        // absent after a crash between acceptance and its first write. Reconstruct a read-only
        // projection from the original spec, including its owner; never redispatch or persist
        // this fallback over a worker's concurrent progress write.
        progress ??= RestoreProgress(job);
        return progress is null ? null : ProjectExecutionOutcome(progress, job);
    }

    private ImportProgress? RestoreProgress(ExecutionJobRecord job)
    {
        if (!FileImportJobParameters.TryParse(job.Spec.Parameters, out var request, out var size, out _)
            || request is null)
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var format = scope.ServiceProvider.GetRequiredService<IFileImportService>().DetectFormat(request.FileName);
        if (format is null)
        {
            return null;
        }

        return ImportProgress.CreateInitial(
            job.OperationId, request.TableName, format.Value, size, request.FileName,
            request.SourceKind, request.SourceUrl, request.CloudFileId, request.UploadId,
            operationInstanceId: request.OperationInstanceId,
            correlationId: request.CorrelationId,
            auditId: request.AuditId,
            proposalId: request.ProposalId,
            ownership: request.Ownership) with
        {
            StartedAt = job.CreatedAt,
            Status = job.Status == ExecutionJobStatus.Running ? ImportStatus.Processing : ImportStatus.Queued,
            Runner = job.Spec.Parameters.GetValueOrDefault(FileImportJobParameters.Runner),
            CurrentPhase = job.Status == ExecutionJobStatus.Running ? "Import running" : "Queued",
        };
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

    public async Task<IReadOnlyList<ImportProgress>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        var active = await _inProcess.GetActiveJobsAsync(cancellationToken).ConfigureAwait(false);
        if (_jobStore is null || active.Count == 0)
        {
            return active;
        }

        // A durable job whose execution record ended without a terminal document (worker crash,
        // timeout, cancel) is no longer active.
        var stillActive = new List<ImportProgress>(active.Count);
        foreach (var progress in active)
        {
            var job = await _jobStore.GetAsync(progress.JobId, cancellationToken).ConfigureAwait(false);
            var projected = job is null ? progress : ProjectExecutionOutcome(progress, job);
            if (!IsTerminal(projected.Status))
            {
                stillActive.Add(projected);
            }
        }

        return stillActive;
    }

    /// <summary>
    /// Applies the background-import queue limits to durable submissions: the active
    /// <see cref="ExecutionJobKind.FileImport"/> records across every host count against
    /// <c>MaxConcurrentBackgroundJobs + MaxBackgroundJobQueueDepth</c>, as the in-process runner
    /// counts its own. Best-effort (not atomic across hosts); a full queue is a 429.
    /// </summary>
    private async Task EnsureAdmittedAsync(ImportLimits limits, CancellationToken cancellationToken)
    {
        var maxOutstanding = Math.Max(1, limits.MaxConcurrentBackgroundJobs) + Math.Max(0, limits.MaxBackgroundJobQueueDepth);
        var active = await _jobStore!.ListActiveAsync(ExecutionJobKind.FileImport, maxOutstanding + 1, cancellationToken)
            .ConfigureAwait(false);
        if (active.Count >= maxOutstanding)
        {
            throw new ImportJobQueueFullException();
        }
    }

    public void Dispose() => (_inProcess as IDisposable)?.Dispose();

    /// <summary>Builds the durable record for a staged import.</summary>
    internal static ExecutionJobRecord BuildJobRecord(
        string jobId,
        ImportRequest request,
        long fileSize,
        BackgroundImportRunnerDecision decision,
        DateTimeOffset now)
    {
        var audit = ControlPlaneTelemetry.WithSubmissionTraceContext(new OperationAuditInfo
        {
            OperationInstanceId = request.OperationInstanceId,
            CorrelationId = request.CorrelationId,
            AuditId = request.AuditId,
            ProposalId = request.ProposalId,
        }, System.Diagnostics.Activity.Current);
        // Persist the same immutable lineage in both carriers. Lease validation
        // reconstructs the exact import parameters from the retained audit record.
        request = request with { CorrelationId = audit.CorrelationId };
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
            AcceptanceDeadline = request.Acceptance?.AcceptBefore,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            // Carries the accepting request's lineage and trace context across the queue boundary
            // so the worker's execution span links back to the upload (#5474).
            Audit = audit,
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
            ExecutionJobStatus.Succeeded when job.FileImportOutput is { FeatureCount: >= 0 } output
                && !string.IsNullOrWhiteSpace(output.PhysicalTableName) && !string.IsNullOrWhiteSpace(output.Schema) => progress with
                {
                    Status = ImportStatus.Completed,
                    CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                    PhysicalTableName = output.PhysicalTableName,
                    Schema = output.Schema,
                    FeaturesProcessed = output.FeatureCount,
                    DetectedSrid = output.DetectedSrid,
                    Warnings = job.Warnings,
                    CurrentPhase = "Import completed",
                },
            // Older jobs and importers without an actual output identity remain explicit:
            // the requested table is not proof of the physical output.
            ExecutionJobStatus.Succeeded => progress with
            {
                Status = ImportStatus.Completed,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                PhysicalTableName = null,
                Schema = null,
                CurrentPhase = "Import finished; output details unavailable",
            },
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
