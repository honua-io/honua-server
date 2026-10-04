from pathlib import Path

def edit(path, old, new, count=-1):
    p=Path(path);s=p.read_text()
    assert old in s, (path,old[:80])
    p.write_text(s.replace(old,new,count))

base='src/Honua.Geoprocessing/Features/Geoprocessing/'
edit(base+'GeoprocessingJobService.cs', 'EnsureSubmissionDidNotRollback(existingByKey);', '''existingByKey = await _dispatcher.RepairLocalDispatchAsync(existingByKey, jobStore, cancellationToken)
                    .ConfigureAwait(false);
                EnsureSubmissionDidNotRollback(existingByKey);''')
edit(base+'GeoprocessingJobService.cs', 'EnsureSubmissionDidNotRollback(priorFormat);', '''priorFormat = await _dispatcher.RepairLocalDispatchAsync(priorFormat, jobStore, cancellationToken)
                    .ConfigureAwait(false);
                EnsureSubmissionDidNotRollback(priorFormat);''')
edit(base+'GeoprocessingJobService.cs', 'EnsureSubmissionDidNotRollback(existingInWindow);', '''existingInWindow = await _dispatcher.RepairLocalDispatchAsync(existingInWindow, jobStore, cancellationToken)
                            .ConfigureAwait(false);
                        EnsureSubmissionDidNotRollback(existingInWindow);''')
edit(base+'GeoprocessingJobService.cs', 'EnsureSubmissionDidNotRollback(existing);', '''existing = await _dispatcher.RepairLocalDispatchAsync(existing, jobStore, cancellationToken)
                            .ConfigureAwait(false);
                        EnsureSubmissionDidNotRollback(existing);''',1)
edit(base+'GeoprocessingJobService.cs', 'MaybeEnqueueLocalAsync(jobId, jobRecord.Spec.Backend, cancellationToken)', 'MaybeEnqueueLocalAsync(jobId, jobRecord.Spec.Backend, cancellationToken, jobRecord.Priority)')
edit(base+'GeoprocessingJobService.cs', 'catch (Exception) when (!cancellationToken.IsCancellationRequested)', '''catch (Exception) when (!cancellationToken.IsCancellationRequested
            || string.Equals(jobRecord.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))''',1)
edit(base+'GeoprocessingJobService.cs', '''            await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                jobStore,''', '''            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                jobStore,''',1)
edit(base+'GeoprocessingJobService.cs', '''failureMessage: "Submission failed.",
                cancellationToken: CancellationToken.None''', '''failureMessage: "Submission failed.",
                logger: _logger,
                cancellationToken: cleanup.Token''',1)
edit(base+'GeoprocessingJobDispatcher.cs','''public async Task MaybeEnqueueLocalAsync(string jobId, string backend, CancellationToken cancellationToken)''','''public async Task MaybeEnqueueLocalAsync(
        string jobId, string backend, CancellationToken cancellationToken, OperationPriority priority = OperationPriority.Normal)''')
edit(base+'GeoprocessingJobDispatcher.cs','await _jobQueue.EnqueueAsync(jobId, cancellationToken: cancellationToken)', 'await _jobQueue.EnqueueAsync(jobId, priority, cancellationToken)')
edit(base+'GeoprocessingJobDispatcher.cs','''    /// <summary>
    /// Best-effort removal of a job''','''    /// <summary>
    /// Repairs an interrupted local admission before acknowledging a keyed replay.
    /// The queue keeps repair idempotent and fences deliveries already claimed by workers.
    /// </summary>
    public async Task<ExecutionJobRecord> RepairLocalDispatchAsync(
        ExecutionJobRecord job, IExecutionJobStore jobStore, CancellationToken cancellationToken)
    {
        if (_jobQueue != null && ExecutionJobSubmissionHelper.NeedsLocalDispatchRepair(job))
        {
            var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
            if (current != null)
            {
                if (ExecutionJobSubmissionHelper.NeedsLocalDispatchRepair(current))
                {
                    await _jobQueue.EnqueueAsync(current.OperationId, current.Priority, cancellationToken).ConfigureAwait(false);
                }
                return current;
            }
        }
        return job;
    }

    /// <summary>
    /// Best-effort removal of a job''')
jobbase='src/Honua.Jobs/Features/ControlPlane/'
edit(jobbase+'ExecutionJobSubmissionHelper.cs','''    public static async Task TryRollbackCreatedJobAsync(''','''    // A persisted initial local Queued record is durable dispatch intent. Both replay
    // and the background sweep repair it after a cancelled request or process loss.
    public static bool NeedsLocalDispatchRepair(ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Queued
            && job.Spec.Kind == ExecutionJobKind.Geoprocessing
            && job.AttemptCount == 0
            && job.ClaimedBy == null
            && !job.CancellationRequestedAt.HasValue
            && string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal);

    public static async Task TryRollbackCreatedJobAsync(''')
edit(jobbase+'ExecutionJobSubmissionHelper.cs','''if (current == null || current.Status is not (ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning))''','''if (current == null || current.Status is not (ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning)
                || ((current.AttemptCount > 0 || current.ClaimedBy != null) && string.Equals(current.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)))''',1)
edit(jobbase+'ExecutionJobSubmissionHelper.cs','''// Best-effort rollback; job TTL or manual intervention will repair. Still log so''','''// Best-effort compensation; the active-job sweep repairs remaining local
            // Queued dispatch intent if the store was unavailable. Still log so''')
edit(jobbase+'JobReconciliationService.cs','''                continue; // Not yet claimed; nothing to reconcile.''','''                // Creation can commit just before request cancellation or process loss.
                // Queued initial local records are dispatch intent, even if no pending
                // queue membership was ever written. Re-read to avoid repairing a stale
                // snapshot; atomic enqueue refuses deliveries already in the claimed set.
                if (ExecutionJobSubmissionHelper.NeedsLocalDispatchRepair(job))
                {
                    var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
                    if (current != null && ExecutionJobSubmissionHelper.NeedsLocalDispatchRepair(current))
                    {
                        await jobQueue.EnqueueAsync(current.OperationId, current.Priority, cancellationToken).ConfigureAwait(false);
                    }
                }
                continue;''')
edit(jobbase+'RedisJobQueue.cs','''    private const string AtomicClaimScript =''','''    private const string AtomicEnqueueScript = """
        if redis.call('ZSCORE', KEYS[2], ARGV[1]) then
            return 0
        end
        return redis.call('ZADD', KEYS[1], 'NX', ARGV[2], ARGV[1])
        """;

    private const string AtomicClaimScript =''')
edit(jobbase+'RedisJobQueue.cs','''await _database.SortedSetAddAsync(QueueKey, operationId, score).ConfigureAwait(false);''','''// Admission replay and reconciliation may repeat enqueue. Preserve the original
        // score and never add a second pending delivery while a worker owns the claim.
        await _database.ScriptEvaluateAsync(AtomicEnqueueScript,
            [QueueKey, ClaimedSetKey], [operationId, score]).ConfigureAwait(false);''',1)
core='src/Honua.Core/Features/ControlPlane/'
edit(core+'Abstractions/IJobExecutor.cs','''    /// <summary>
    /// Verifies that the worker still owns''','''    /// <summary>
    /// Records a receipt for a sink transaction that has already committed. Durable
    /// contexts preserve this evidence after operator cancellation, while retaining
    /// worker ownership and attempt fences. Rejection must be surfaced to the executor.
    /// Use an independent cleanup token after commit rather than the cancelled job token.
    /// </summary>
    async Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        if (!await TryPublishArtifactAsync(artifactReference, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The execution fence rejected a committed-effect receipt.");
        }
    }

    /// <summary>
    /// Verifies that the worker still owns''')
edit(core+'Domain/OperationModels.cs','''    public IReadOnlyList<string> ArtifactReferences { get; init; } = Array.Empty<string>();''','''    public IReadOnlyList<string> ArtifactReferences { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Receipts for already committed sink effects, also exposed in artifact references.
    /// These survive operator cancellation and distinguish committed work from fenced output.
    /// </summary>
    public IReadOnlyList<string> CommittedEffectReferences { get; init; } = Array.Empty<string>();''',1)
edit(jobbase+'JobExecutionService.cs','''    public async Task<bool> TryPublishArtifactAsync(
        string artifactReference,
        CancellationToken cancellationToken = default)
    {''','''    public Task<bool> TryPublishArtifactAsync(
        string artifactReference,
        CancellationToken cancellationToken = default)
        => TryPublishReferenceAsync(artifactReference, committedEffect: false, cancellationToken);

    /// <inheritdoc />
    public async Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        if (!await TryPublishReferenceAsync(artifactReference, committedEffect: true, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"The execution fence rejected a committed-effect receipt for job '{operationId}'.");
        }
    }

    private async Task<bool> TryPublishReferenceAsync(
        string artifactReference, bool committedEffect, CancellationToken cancellationToken)
    {''')
edit(jobbase+'JobExecutionService.cs','''                if (job.CancellationRequestedAt.HasValue)
                {
                    // Durable cancellation wins:''','''                if (!committedEffect && job.CancellationRequestedAt.HasValue)
                {
                    // Durable cancellation wins:''',1)
edit(jobbase+'JobExecutionService.cs','''                if (!TryAppendArtifactReference(job.ArtifactReferences, artifactReference, out var refs))
                {
                    // Identical publication already durable — retried publish is a no-op.
                    return true;
                }

                var updated = job with
                {
                    ArtifactReferences = refs,''','''                var effects = job.CommittedEffectReferences;
                if (committedEffect && effects.Contains(artifactReference, StringComparer.Ordinal))
                {
                    return true;
                }
                var appended = TryAppendArtifactReference(job.ArtifactReferences, artifactReference, out var refs);
                if (!appended && !committedEffect)
                {
                    // Identical publication already durable — retried publish is a no-op.
                    return true;
                }

                var updated = job with
                {
                    ArtifactReferences = refs,
                    CommittedEffectReferences = committedEffect ? [.. effects, artifactReference] : effects,''')
edit(jobbase+'JobExecutionService.cs','''        // Durable cancellation wins over a racing success (#3089): the artifact
        // publication fence already refuses to publish once CancellationRequestedAt
        // is stamped, so finalizing this record as Succeeded would durably expose a
        // success with silently missing outputs and no repair path. Honour the stamp
        // and finalize as Cancelled instead — consistent with every other path that
        // observes the durable signal.''','''        // Ordinary output remains fenced by durable cancellation. A completed sink
        // with an already committed receipt instead reports its actual successful effect;
        // the cancellation stamp and warning explain why cancellation could not undo it.''')
edit(jobbase+'JobExecutionService.cs','''        var effectiveStatus = result.Status;
        if (result.Status == ExecutionJobStatus.Succeeded && job.CancellationRequestedAt.HasValue)''','''        var effectiveStatus = result.Status;
        var committedAfterCancellation = job.CancellationRequestedAt.HasValue && job.CommittedEffectReferences.Count > 0;
        if (result.Status == ExecutionJobStatus.Succeeded && job.CancellationRequestedAt.HasValue && !committedAfterCancellation)''')
edit(jobbase+'JobExecutionService.cs','''            Warnings = result.Warnings,
            PercentComplete''','''            Warnings = committedAfterCancellation
                ? [.. result.Warnings, CommittedCancellationWarning]
                : result.Warnings,
            PercentComplete''',1)
edit(jobbase+'JobExecutionService.cs','''                ExecutionJobStatus.Succeeded => "Completed",''','''                ExecutionJobStatus.Succeeded when committedAfterCancellation => "Completed with committed effects (cancellation requested)",
                ExecutionJobStatus.Succeeded => "Completed",''',1)
edit(jobbase+'JobExecutionService.cs','''    private async Task TerminateJobAsync(''','''    private const string CommittedCancellationWarning = "Cancellation requested after sink data committed; committed-effect receipts are retained.";

    private async Task TerminateJobAsync(''')
edit(jobbase+'JobExecutionService.cs','''            ErrorMessage = reason,
            ArtifactReferences''','''            ErrorMessage = terminalStatus == ExecutionJobStatus.Cancelled && job.CommittedEffectReferences.Count > 0
                ? CommittedCancellationWarning : reason,
            Warnings = terminalStatus == ExecutionJobStatus.Cancelled && job.CommittedEffectReferences.Count > 0
                ? [.. job.Warnings, CommittedCancellationWarning] : job.Warnings,
            ArtifactReferences''',1)
edit(jobbase+'JobExecutionService.cs','''            CurrentPhase = terminalStatus == ExecutionJobStatus.Cancelled ? "Cancelled" : "Failed"''','''            CurrentPhase = terminalStatus == ExecutionJobStatus.Cancelled
                ? job.CommittedEffectReferences.Count > 0 ? "Cancelled after committed effects" : "Cancelled"
                : "Failed"''',1)
edit(jobbase+'JobExecutionService.cs','''                ErrorMessage = "Cancelled by operator (durable signal honoured during abandon).",
                CurrentPhase = "Cancelled"''','''                ErrorMessage = current.CommittedEffectReferences.Count > 0
                    ? CommittedCancellationWarning : "Cancelled by operator (durable signal honoured during abandon).",
                Warnings = current.CommittedEffectReferences.Count > 0
                    ? [.. current.Warnings, CommittedCancellationWarning] : current.Warnings,
                CurrentPhase = current.CommittedEffectReferences.Count > 0 ? "Cancelled after committed effects" : "Cancelled"''')
edit(jobbase+'JobExecutionService.cs','''                ArtifactReferences = Array.Empty<string>(),
                Warnings = Array.Empty<string>(),''','''                ArtifactReferences = latestBeforeRequeue.CommittedEffectReferences,
                Warnings = latestBeforeRequeue.CommittedEffectReferences.Count > 0
                    ? latestBeforeRequeue.Warnings : Array.Empty<string>(),''',1)
edit(base+'Execution/WorkspaceRoutingJobExecutionContext.cs','''    public Task ThrowIfExecutionLeaseLostAsync''','''    // A committed receipt is job evidence, not a new workspace output. Persist it
    // directly so workspace cancellation/collision policy cannot hide a real sink effect.
    public Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
        => _inner.RecordCommittedEffectAsync(artifactReference, cancellationToken);

    public Task ThrowIfExecutionLeaseLostAsync''')
edit(base+'Execution/HonuaLayerSinkExecutor.cs','''        await context.PublishArtifactAsync(
            SinkResultArtifact.Build(''','''        await context.RecordCommittedEffectAsync(
            SinkResultArtifact.Build(''',1)
edit(base+'Execution/ExternalPostgisSinkExecutor.cs','''        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(
            SinkResultArtifact.Build(''','''        // The transaction has committed. Cancellation may stop later work, but must
        // not suppress this destination receipt or misreport the committed load.
        await context.RecordCommittedEffectAsync(
            SinkResultArtifact.Build(''',1)
edit(base+'Execution/ExternalPostgisSinkExecutor.cs','''                ("table", table),
                ("featuresWritten", written),''','''                ("table", table),
                ("batchId", batchId),
                ("featuresWritten", written),''',1)
edit(base+'Execution/ExternalPostgisSinkExecutor.cs','''            cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", cancellationToken)''','''            CancellationToken.None).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", CancellationToken.None)''',1)
edit(base+'GeoprocessingResultPackageFactory.cs','''        var artifacts = job.Status == ExecutionJobStatus.Succeeded
            ? BuildArtifacts(job, processCatalog)
            : [];''','''        var artifacts = job.Status == ExecutionJobStatus.Succeeded || job.CommittedEffectReferences.Count > 0
            ? BuildArtifacts(job, processCatalog)
            : [];
        if (job.Status != ExecutionJobStatus.Succeeded)
        {
            // Cancelled/failed work exposes only receipts for effects that already
            // committed, retaining their original output slot and artifact identity.
            artifacts = artifacts.Where((_, index) => job.CommittedEffectReferences.Contains(
                job.ArtifactReferences[index], StringComparer.Ordinal)).ToArray();
        }
        var committedSummary = job.CommittedEffectReferences.Count > 0
            ? " Sink effects committed; committed-effect receipts are retained." : string.Empty;''')
edit(base+'GeoprocessingResultPackageFactory.cs','''                    Description = artifacts.Length == 1
                        ? "Produced 1 artifact."
                        : $"Produced {artifacts.Length} artifacts."''','''                    Description = (artifacts.Length == 1
                        ? "Produced 1 artifact."
                        : $"Produced {artifacts.Length} artifacts.")
                        + (job.CancellationRequestedAt.HasValue ? committedSummary : string.Empty)''')
edit(base+'GeoprocessingResultPackageFactory.cs','''                    Description = job.ErrorMessage ?? "The geoprocessing job failed."''','''                    Description = (job.ErrorMessage ?? "The geoprocessing job failed.") + committedSummary''',1)
edit(base+'GeoprocessingResultPackageFactory.cs','''                provenance),
            ExecutionJobStatus.Cancelled => new AnalysisResultPackage''','''                provenance) with { Artifacts = artifacts },
            ExecutionJobStatus.Cancelled => new AnalysisResultPackage''',1)
edit(base+'GeoprocessingResultPackageFactory.cs','''                    Description = job.ErrorMessage ?? "The geoprocessing job was cancelled."''','''                    Description = (job.ErrorMessage ?? "The geoprocessing job was cancelled.") + committedSummary''',1)
edit(base+'GeoprocessingResultPackageFactory.cs','''                Provenance = provenance,
                Errors =''','''                Provenance = provenance,
                Artifacts = artifacts,
                Errors =''',1)
edit(jobbase+'JobExecutionService.cs','''                var appended = TryAppendArtifactReference(job.ArtifactReferences, artifactReference, out var refs);
                if (!appended && !committedEffect)''','''                var appended = TryAppendArtifactReference(job.ArtifactReferences, artifactReference, out var refs);
                if (!appended)
                {
                    refs = [.. job.ArtifactReferences];
                }
                if (!appended && !committedEffect)''')
edit(jobbase+'JobExecutionService.cs','''    private const string CommittedCancellationWarning''','''    internal const string CommittedCancellationWarning''')
edit(jobbase+'JobExecutionService.cs','''            ArtifactReferences = reason is "license expired" or DrainDeadlineFailureMessage ? [] : job.ArtifactReferences,''','''            ArtifactReferences = reason switch
            {
                "license expired" => [],
                DrainDeadlineFailureMessage => job.CommittedEffectReferences,
                _ => job.ArtifactReferences
            },
            CommittedEffectReferences = reason == "license expired" ? [] : job.CommittedEffectReferences,''')
edit(jobbase+'JobReconciliationService.cs','''                ArtifactReferences = Array.Empty<string>(),''','''                ArtifactReferences = preRetry.CommittedEffectReferences,''',1)
edit(jobbase+'JobReconciliationService.cs','''            ErrorMessage = "Cancelled by operator (durable signal honoured by reconciler).",
            CurrentPhase = "Cancelled"''','''            ErrorMessage = job.CommittedEffectReferences.Count > 0
                ? JobExecutionService.CommittedCancellationWarning : "Cancelled by operator (durable signal honoured by reconciler).",
            Warnings = job.CommittedEffectReferences.Count > 0
                ? [.. job.Warnings, JobExecutionService.CommittedCancellationWarning] : job.Warnings,
            CurrentPhase = job.CommittedEffectReferences.Count > 0 ? "Cancelled after committed effects" : "Cancelled"''')

edit(base+'Execution/HonuaLayerSinkExecutor.cs', '("batchId", outcome.BatchId),', '("batchId", outcome.BatchId),\n                ("committed", true),',1)
edit(base+'Execution/ExternalPostgisSinkExecutor.cs', '("table", table),\n                ("batchId", batchId),', '("table", table),\n                ("batchId", batchId),\n                ("committed", true),',1)
edit(core+'Abstractions/IJobExecutor.cs', '''    /// <summary>
    /// Creates a successful result.''', '''    /// <summary>
    /// Whether successful execution completed by committing sink effects. A later
    /// operator cancellation cannot undo this completion. Finalization also requires
    /// a durable committed-effect receipt before honouring this indication.
    /// </summary>
    public bool CompletedWithCommittedEffects { get; init; }

    /// <summary>
    /// Creates a successful result.''',1)
edit(base+'Execution/HonuaLayerSinkExecutor.cs', 'return JobExecutionResult.Succeeded();', 'return JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true };',1)
edit(base+'Execution/ExternalPostgisSinkExecutor.cs', 'return JobExecutionResult.Succeeded();', 'return JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true };',1)
edit(jobbase+'JobExecutionService.cs', '''        var committedAfterCancellation = job.CancellationRequestedAt.HasValue && job.CommittedEffectReferences.Count > 0;''', '''        var hasCommittedCancellation = job.CancellationRequestedAt.HasValue && job.CommittedEffectReferences.Count > 0;
        var committedAfterCancellation = result.CompletedWithCommittedEffects && hasCommittedCancellation;''')
edit(jobbase+'JobExecutionService.cs', '''                ExecutionJobStatus.Cancelled => "Cancelled by operator (durable signal honoured at finalization).",''', '''                ExecutionJobStatus.Cancelled when hasCommittedCancellation => CommittedCancellationWarning,
                ExecutionJobStatus.Cancelled => "Cancelled by operator (durable signal honoured at finalization).",''',1)
edit(jobbase+'JobExecutionService.cs', '''            Warnings = committedAfterCancellation''', '''            Warnings = hasCommittedCancellation''',1)
edit(jobbase+'JobExecutionService.cs', '''                ExecutionJobStatus.Cancelled => "Cancelled",''', '''                ExecutionJobStatus.Cancelled when hasCommittedCancellation => "Cancelled after committed effects",
                ExecutionJobStatus.Cancelled => "Cancelled",''',1)
edit(base+'Execution/WorkspaceRoutingJobExecutionContext.cs', '''    // A committed receipt is job evidence, not a new workspace output. Persist it
    // directly so workspace cancellation/collision policy cannot hide a real sink effect.
    public Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
        => _inner.RecordCommittedEffectAsync(artifactReference, cancellationToken);''', '''    // Persist the receipt before workspace indexing so even a workspace collision
    // cannot hide the committed sink effect from the job's terminal result.
    public async Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
    {
        await _inner.RecordCommittedEffectAsync(artifactReference, cancellationToken).ConfigureAwait(false);
        await PublishWorkspaceReferenceAsync(artifactReference, committedEffect: true, cancellationToken).ConfigureAwait(false);
    }''')
edit(base+'Execution/WorkspaceRoutingJobExecutionContext.cs', '''    public async Task<bool> TryPublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
    {''', '''    public Task<bool> TryPublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        => PublishWorkspaceReferenceAsync(artifactReference, committedEffect: false, cancellationToken);

    private async Task<bool> PublishWorkspaceReferenceAsync(
        string artifactReference, bool committedEffect, CancellationToken cancellationToken)
    {''')
edit(base+'Execution/WorkspaceRoutingJobExecutionContext.cs', '''            ct => _inner.TryPublishArtifactAsync(artifactReference, ct), cancellationToken).ConfigureAwait(false);''', '''            ct => committedEffect ? Task.FromResult(true) : _inner.TryPublishArtifactAsync(artifactReference, ct),
            cancellationToken).ConfigureAwait(false);''')

edit(jobbase+'ExecutionJobSubmissionHelper.cs', 'Best-effort rollback of execution job {OperationId} failed; job TTL or manual intervention will repair', 'Best-effort compensation of execution job {OperationId} failed')
edit('src/Honua.Worker.Gdal/Execution/GdalStagedOutputContext.cs', '''    public Task ThrowIfExecutionLeaseLostAsync''', '''    public Task RecordCommittedEffectAsync(string artifactReference, CancellationToken cancellationToken = default)
        => _inner.RecordCommittedEffectAsync(artifactReference, cancellationToken);

    public Task ThrowIfExecutionLeaseLostAsync''',1)
