// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Tiles;

/// <summary>
/// Protocol-neutral tile-export job lifecycle over the canonical execution store/queue.
/// See <see cref="ITileExportJobService"/> for the contract.
/// </summary>
internal sealed partial class TileExportJobService : ITileExportJobService
{
    // Phase marker written on the record when submission fails before the job is durably
    // queued, so an idempotent replay of the same key does not silently return a half-created
    // job that never dispatched.
    private const string SubmissionFailurePhase = "Failed (submission)";
    private const int CancellationCasAttempts = 3;

    // Tiles charged per admission cost unit. Normalizes the checked tile count onto the shared
    // admission cost scale (where a geoprocessing plan step is one unit) so ordinary tile exports
    // are admissible while enormous ones are still gated per partition.
    private const long AdmissionTilesPerCostUnit = 1000;

    private readonly TimeProvider _timeProvider;
    private readonly IOptions<CloudStorageOptions> _storageOptions;
    private readonly ILogger<TileExportJobService> _logger;
    private readonly IExecutionJobStore? _jobStore;
    private readonly IJobQueue? _jobQueue;
    private readonly ICloudFileStorage? _storage;
    private readonly IExecutionAdmissionEvaluator? _admissionEvaluator;

    public TileExportJobService(
        TimeProvider timeProvider,
        IOptions<CloudStorageOptions> storageOptions,
        ILogger<TileExportJobService> logger,
        IExecutionJobStore? jobStore = null,
        IJobQueue? jobQueue = null,
        ICloudFileStorage? storage = null,
        IExecutionAdmissionEvaluator? admissionEvaluator = null)
    {
        _timeProvider = timeProvider;
        _storageOptions = storageOptions;
        _logger = logger;
        _jobStore = jobStore;
        _jobQueue = jobQueue;
        _storage = storage;
        _admissionEvaluator = admissionEvaluator;
    }

    public async Task<ExecutionJobRecord> SubmitAsync(
        TileExportJobPlan plan,
        string? idempotencyKey,
        string? correlationId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(principal);

        // Build validates the plan and computes the exact-key spec + content identity. Surface
        // contract violations as a sanitized validation error rather than a raw ArgumentException.
        ExecutionJobSpec spec;
        try
        {
            spec = TileExportExecutionSpecBuilder.Build(plan);
        }
        catch (ArgumentException exception)
        {
            throw new TileExportValidationException(exception.Message);
        }

        var jobStore = RequireJobStore();
        var principalId = ResolvePrincipalId(principal);
        var resolvedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        var jobId = CreateJobId(resolvedKey);

        // The plan's content identity is a complete, canonical fingerprint of every byte-affecting
        // input, so it doubles as the idempotent-request fingerprint. A same-key replay whose plan
        // hashes differently is a payload mismatch and is rejected.
        var requestFingerprint = TileExportArtifactIdentity.Compute(plan);
        var partitionKey = BuildPartitionKey(plan);

        await EnsureAdmittedAsync(plan, principalId, partitionKey, cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var record = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            Priority = OperationPriority.Normal,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                RequestedBy = principalId,
                IdempotencyKey = resolvedKey,
                CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim(),
                RequestFingerprint = requestFingerprint
            },
            // The recognized admission envelope is persisted on the first-class concurrency
            // partition rather than in the spec parameters, so the exact-key tile-export contract
            // is never widened. The evaluator reads this partition when summing active concurrency.
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = partitionKey,
                RequiresExclusiveLease = false
            },
            Spec = spec
        };

        var created = await jobStore.TryCreateAsync(record, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            var existing = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
                ?? throw new TileExportStoreUnavailableException(
                    "Tile-export job could not be created or located during idempotent submission.");
            EnsureMatchingIdempotentRequest(existing, requestFingerprint, principalId);
            EnsureSubmissionDidNotRollback(existing);
            Log.SubmittedIdempotent(_logger, jobId);
            return existing;
        }

        try
        {
            // The tile-export runtime targets the in-process worker (backend "local"): enqueue for
            // the claim loop. A remote backend, when configured, is driven by the reconciler off the
            // same durable record — no protocol-local dispatch path is introduced here.
            if (_jobQueue is not null)
            {
                await _jobQueue.EnqueueAsync(jobId, record.Priority, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await TryRollbackAsync(jobId).ConfigureAwait(false);
            throw;
        }

        Log.Submitted(_logger, jobId, plan.SourceKind.ToString(), plan.ResourceId);
        return record;
    }

    public async Task<ExecutionJobRecord> GetStatusAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var job = await RequireBoundJobAsync(jobId, scope, principal, cancellationToken).ConfigureAwait(false);
        Log.Retrieved(_logger, jobId, job.Status.ToString());
        return job;
    }

    public async Task<TileExportResultArtifact> GetResultAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var job = await RequireBoundJobAsync(jobId, scope, principal, cancellationToken).ConfigureAwait(false);

        if (job.Status != ExecutionJobStatus.Succeeded)
        {
            throw new TileExportPreconditionFailedException(
                $"Tile-export job '{jobId}' has not completed successfully (current status: {job.Status}).");
        }

        if (_storage is null)
        {
            throw new TileExportStoreUnavailableException(
                "Tile-export artifact storage is not configured; the result cannot be delivered.");
        }

        var artifactReference = job.ArtifactReferences.Count > 0 ? job.ArtifactReferences[0] : null;
        if (string.IsNullOrWhiteSpace(artifactReference))
        {
            throw new TileExportNotFoundException($"Tile-export job '{jobId}' has no published artifact.");
        }

        // Verify the artifact still exists and has not expired before minting a URL. Expiry maps to
        // not-found so an aged job is indistinguishable from one that never existed.
        var metadata = await _storage.GetMetadataAsync(artifactReference, cancellationToken).ConfigureAwait(false);
        if (metadata is null || (metadata.ExpiresAt is { } expiresAt && expiresAt <= _timeProvider.GetUtcNow()))
        {
            Log.ArtifactExpired(_logger, jobId);
            throw new TileExportNotFoundException($"Tile-export artifact for job '{jobId}' is no longer available.");
        }

        var signedUrl = await _storage
            .GetPresignedUrlAsync(artifactReference, _storageOptions.Value.SignedUrlLifetime, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(signedUrl))
        {
            throw new TileExportNotFoundException($"Tile-export artifact for job '{jobId}' is no longer available.");
        }

        Log.ResultDelivered(_logger, jobId);
        return new TileExportResultArtifact(signedUrl, metadata.ExpiresAt, ResolvePackageFormat(job), metadata.SizeBytes);
    }

    public async Task CancelAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var jobStore = RequireJobStore();
        var job = await RequireBoundJobAsync(jobId, scope, principal, cancellationToken).ConfigureAwait(false);

        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            await TryRemoveFromQueueAsync(jobId, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsTerminal(job.Status))
        {
            throw new TileExportPreconditionFailedException(
                $"Tile-export job '{jobId}' is in terminal state '{job.Status}' and cannot be cancelled.");
        }

        // Bounded CAS loop mirroring the shared cancellation helper: stamp a durable cancellation
        // signal for a claimed/running job (the worker and reconciler honor it), or transition an
        // unclaimed queued job straight to cancelled. Re-read on version conflict, and re-check the
        // terminal race each attempt so a job that completes mid-cancel fails the precondition.
        for (var attempt = 0; attempt < CancellationCasAttempts; attempt++)
        {
            if (job.Status == ExecutionJobStatus.Cancelled)
            {
                await TryRemoveFromQueueAsync(jobId, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsTerminal(job.Status))
            {
                throw new TileExportPreconditionFailedException(
                    $"Tile-export job '{jobId}' reached terminal state '{job.Status}' before cancellation could be applied.");
            }

            var now = _timeProvider.GetUtcNow();
            var claimed = !string.IsNullOrEmpty(job.ClaimedBy);
            var updated = claimed
                ? job with
                {
                    CancellationRequestedAt = job.CancellationRequestedAt ?? now,
                    UpdatedAt = now,
                    CurrentPhase = "Cancelling"
                }
                : job with
                {
                    Status = ExecutionJobStatus.Cancelled,
                    UpdatedAt = now,
                    CompletedAt = now,
                    CurrentPhase = "Cancelled"
                };

            if (await jobStore.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (!claimed)
                {
                    await TryRemoveFromQueueAsync(jobId, cancellationToken).ConfigureAwait(false);
                    Log.Cancelled(_logger, jobId);
                }
                else
                {
                    Log.CancellationRequested(_logger, jobId);
                }

                return;
            }

            var reread = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (reread is null)
            {
                throw new TileExportNotFoundException($"Tile-export job '{jobId}' was deleted during cancellation.");
            }

            job = reread;
        }

        throw new TileExportPreconditionFailedException(
            $"Tile-export job '{jobId}' cancellation could not be confirmed after {CancellationCasAttempts} attempts.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task EnsureAdmittedAsync(
        TileExportJobPlan plan,
        string? principalId,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        if (_admissionEvaluator is null)
        {
            return;
        }

        // Cost is derived from the checked selected-tile count from the bounded grid planner (which
        // never materializes the grid), then normalized to ~1 unit per 1,000 tiles. The shared
        // admission cost limit is tuned to job-relative weights (a geoprocessing plan step is one
        // unit); charging one unit per thousand tiles keeps ordinary exports admissible while a
        // partition still caps at roughly limit x 1,000 concurrent tiles, so an enormous export is
        // gated rather than every export being denied under a step-count-scaled limit.
        var grid = TileExportGridPlanner.Create(plan);
        var request = new ExecutionAdmissionRequest
        {
            JobKind = ExecutionJobKind.TileExport,
            PartitionKey = partitionKey,
            PrincipalId = principalId,
            EstimatedCostWeight = Math.Max(1d, Math.Ceiling(grid.SelectedTileCount / (double)AdmissionTilesPerCostUnit)),
            Priority = OperationPriority.Normal
        };

        var decision = await _admissionEvaluator.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        if (decision.Outcome == ExecutionAdmissionOutcome.Admitted)
        {
            return;
        }

        Log.AdmissionRejected(
            _logger,
            decision.Outcome.ToString(),
            decision.DenyingDimension?.ToString() ?? "Unknown",
            decision.PolicyRef ?? "unknown");

        throw new TileExportAdmissionException(
            decision.Outcome,
            decision.DenyingDimension ?? ExecutionAdmissionDimension.Backpressure,
            decision.PolicyRef ?? "unknown",
            decision.Reason ?? "Execution admission rejected the tile-export request.",
            decision.RetryAfterSeconds ?? 10);
    }

    private async Task<ExecutionJobRecord> RequireBoundJobAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new TileExportValidationException("Tile-export job identifier is required.");
        }

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);

        // Ownership, source/resource binding, and existence all collapse to the same sanitized
        // not-found so a caller cannot distinguish "does not exist" from "not yours" or
        // "different service" — closing the cross-principal/cross-resource probing channel.
        if (job is null || !MatchesBinding(job, scope) || !IsOwnedBy(job, principal))
        {
            Log.NotFound(_logger, jobId);
            throw new TileExportNotFoundException($"Tile-export job '{jobId}' not found.");
        }

        return job;
    }

    private static bool MatchesBinding(ExecutionJobRecord job, TileExportJobScope scope)
    {
        if (job.Spec.Kind != ExecutionJobKind.TileExport)
        {
            return false;
        }

        return job.Spec.Parameters.TryGetValue(TileExportJobParameterKeys.SourceKind, out var sourceKind)
            && string.Equals(sourceKind, scope.SourceKind.ToString(), StringComparison.Ordinal)
            && job.Spec.Parameters.TryGetValue(TileExportJobParameterKeys.ResourceId, out var resourceId)
            && string.Equals(resourceId, scope.ResourceId, StringComparison.Ordinal);
    }

    private static bool IsOwnedBy(ExecutionJobRecord job, ClaimsPrincipal principal)
    {
        if (principal.IsInRole("admin"))
        {
            return true;
        }

        var owner = job.Audit.RequestedBy;
        // An ownerless job (no recorded submitter) is reachable only by admin, matching the
        // geoprocessing lifecycle so a coarse grant cannot enumerate unattributed jobs.
        return !string.IsNullOrWhiteSpace(owner)
            && string.Equals(owner, ResolvePrincipalId(principal), StringComparison.Ordinal);
    }

    private static TileExportPackageFormat ResolvePackageFormat(ExecutionJobRecord job)
        => job.Spec.Parameters.TryGetValue(TileExportJobParameterKeys.PackageFormat, out var raw)
            && Enum.TryParse(raw, ignoreCase: false, out TileExportPackageFormat format)
                ? format
                : TileExportPackageFormat.Zip;

    private static string BuildPartitionKey(TileExportJobPlan plan)
        => $"tile-export:{plan.SourceKind.ToString().ToLowerInvariant()}:{plan.ResourceId}";

    private async Task TryRollbackAsync(string jobId)
    {
        try
        {
            var jobStore = _jobStore;
            if (jobStore is null)
            {
                return;
            }

            var current = await jobStore.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || current.Status is not (ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning))
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            var failed = current with
            {
                Status = ExecutionJobStatus.Failed,
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = "Tile-export submission failed before the job was durably queued.",
                CurrentPhase = SubmissionFailurePhase
            };

            await jobStore.TrySetAsync(failed, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await TryRemoveFromQueueAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Best-effort rollback; the job TTL or the reconciler repairs any residue. Logged so the
            // failure is diagnosable rather than silently swallowed.
            Log.RollbackFailed(_logger, jobId, exception);
        }
    }

    private async Task TryRemoveFromQueueAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_jobQueue is null)
        {
            return;
        }

        try
        {
            await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Best-effort removal — the stale-claim reconciler repairs any queue residue.
            Log.QueueRemovalFailed(_logger, jobId, exception);
        }
    }

    private static void EnsureMatchingIdempotentRequest(
        ExecutionJobRecord existing,
        string requestFingerprint,
        string? principalId)
    {
        // A different principal must never silently receive another caller's job through an
        // idempotency-key collision.
        var owner = existing.Audit.RequestedBy;
        if (!string.IsNullOrWhiteSpace(owner) && !string.Equals(owner, principalId, StringComparison.Ordinal))
        {
            throw new TileExportIdempotencyConflictException();
        }

        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint)
            && string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new TileExportIdempotencyConflictException(existing.OperationId);
    }

    private static void EnsureSubmissionDidNotRollback(ExecutionJobRecord existing)
    {
        if (existing.Status == ExecutionJobStatus.Failed
            && string.Equals(existing.CurrentPhase, SubmissionFailurePhase, StringComparison.Ordinal))
        {
            throw new TileExportPreconditionFailedException(
                $"Tile-export job '{existing.OperationId}' submission previously failed before queueing. " +
                "Retry with a new idempotency key.");
        }
    }

    private IExecutionJobStore RequireJobStore()
        => _jobStore ?? throw new TileExportStoreUnavailableException();

    private static string CreateJobId(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return $"te-{Guid.NewGuid():N}";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey.Trim()));
        return $"te-{Convert.ToHexStringLower(hash.AsSpan(0, 12))}";
    }

    private static string? ResolvePrincipalId(ClaimsPrincipal principal)
        => principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.Identity?.Name;

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;

    private static partial class Log
    {
        [LoggerMessage(9270, LogLevel.Information, "Submitted tile-export job {JobId} ({SourceKind}:{ResourceId})")]
        internal static partial void Submitted(ILogger logger, string jobId, string sourceKind, string resourceId);

        [LoggerMessage(9271, LogLevel.Information, "Returned existing tile-export job {JobId} for idempotent submission")]
        internal static partial void SubmittedIdempotent(ILogger logger, string jobId);

        [LoggerMessage(9272, LogLevel.Debug, "Retrieved tile-export job {JobId} with status {Status}")]
        internal static partial void Retrieved(ILogger logger, string jobId, string status);

        [LoggerMessage(9273, LogLevel.Information, "Delivered tile-export result for job {JobId}")]
        internal static partial void ResultDelivered(ILogger logger, string jobId);

        [LoggerMessage(9274, LogLevel.Information, "Cancelled tile-export job {JobId}")]
        internal static partial void Cancelled(ILogger logger, string jobId);

        [LoggerMessage(9275, LogLevel.Information, "Requested cancellation of running tile-export job {JobId}")]
        internal static partial void CancellationRequested(ILogger logger, string jobId);

        [LoggerMessage(9276, LogLevel.Information, "Tile-export job {JobId} not found or not visible to caller")]
        internal static partial void NotFound(ILogger logger, string jobId);

        [LoggerMessage(9277, LogLevel.Information, "Tile-export artifact for job {JobId} is missing or expired")]
        internal static partial void ArtifactExpired(ILogger logger, string jobId);

        [LoggerMessage(9278, LogLevel.Warning, "Tile-export submission rejected by admission ({Outcome}/{Dimension}/{PolicyRef})")]
        internal static partial void AdmissionRejected(ILogger logger, string outcome, string dimension, string policyRef);

        [LoggerMessage(9279, LogLevel.Warning, "Best-effort rollback of tile-export job {JobId} failed; TTL or reconciler will repair")]
        internal static partial void RollbackFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(9280, LogLevel.Warning, "Best-effort queue removal for tile-export job {JobId} failed")]
        internal static partial void QueueRemovalFailed(ILogger logger, string jobId, Exception exception);
    }
}
