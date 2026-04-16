// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure;
using Honua.Server.Features.Infrastructure.ControlPlane;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Shared implementation of geoprocessing job lifecycle operations.
/// Consumed by gRPC (<see cref="HonuaProcessService"/>) and REST
/// (<c>GPServerEndpoints</c>) adapters.
/// </summary>
internal sealed class GeoprocessingJobService : IGeoprocessingJobService
{
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(7);

    private readonly IExecutionJobStore? _jobStore;
    private readonly IJobQueue? _jobQueue;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IReadOnlyList<IJobCancellationNotifier> _cancellationNotifiers;
    private readonly IOperatorAuthorizationEvaluator _authEvaluator;
    private readonly IOperatorApprovalEvaluator _approvalEvaluator;
    private readonly ILogger<GeoprocessingJobService> _logger;

    public GeoprocessingJobService(
        IUniversalProgressStore progressStore,
        IEnumerable<IJobCancellationNotifier> cancellationNotifiers,
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        ILogger<GeoprocessingJobService> logger,
        IExecutionJobStore? jobStore = null,
        IJobQueue? jobQueue = null)
    {
        _progressStore = progressStore;
        _cancellationNotifiers = cancellationNotifiers.ToArray();
        _authEvaluator = authEvaluator;
        _approvalEvaluator = approvalEvaluator;
        _logger = logger;
        _jobStore = jobStore;
        _jobQueue = jobQueue;
    }

    public void EnsureCallerAuthorized(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation)
    {
        EnsureAuthorized(principal, resourceType, operation);
    }

    public PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal)
    {
        ValidatePlanStructure(plan);

        var violations = new List<GeoprocessingValidationFailure>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "EMPTY_PLAN_ID",
                Message = "Plan identifier is required.",
                FieldPath = "plan_id"
            });
        }

        if (plan.Steps.Count == 0)
        {
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "EMPTY_STEPS",
                Message = "Plan must contain at least one step.",
                FieldPath = "steps"
            });
        }

        var approvalReq = _approvalEvaluator.Evaluate(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Process,
                Operation = OperatorOperation.Execute
            });

        var result = new PlanValidationResult
        {
            IsExecutable = violations.Count == 0,
            RequiresApproval = approvalReq.IsRequired,
            Violations = violations,
            Warnings = warnings
        };

        GeoprocessingServiceLog.PlanValidated(_logger, plan.PlanId ?? "", result.IsExecutable, violations.Count);

        return result;
    }

    public DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal)
    {
        ValidatePlanStructure(plan);

        var result = new DryRunResult
        {
            EstimatedDurationSeconds = 0,
            EstimatedArtifacts = plan.Outputs,
            SideEffects = []
        };

        GeoprocessingServiceLog.DryRunCompleted(_logger, plan.PlanId, result.EstimatedDurationSeconds);

        return result;
    }

    public async Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePlanStructure(plan);
        EnsurePlanExecutable(plan);
        EnsureApproved(principal);

        var jobStore = RequireJobStore();
        var now = DateTimeOffset.UtcNow;
        var resolvedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        var jobId = CreateJobId(resolvedKey);
        var requestFingerprint = CreateRequestFingerprint(plan);

        var specParams = protocolMetadata != null
            ? new Dictionary<string, string>(protocolMetadata)
            : new Dictionary<string, string>();

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                IdempotencyKey = resolvedKey,
                RequestedBy = principal.Identity?.Name,
                RequestFingerprint = requestFingerprint
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = $"geoprocessing:{plan.PlanId}",
                Parameters = specParams
            }
        };

        var created = await jobStore.TryCreateAsync(jobRecord, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!created)
        {
            var existing = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                EnsureMatchingIdempotentRequest(existing, requestFingerprint);
                EnsureSubmissionDidNotRollback(existing);
                GeoprocessingServiceLog.JobSubmittedIdempotent(_logger, jobId);
                return existing;
            }

            throw new InvalidOperationException("Failed to create or locate execution job.");
        }

        try
        {
            var progress = GeoprocessingProgress.CreateForSubmittedJob(jobId, plan.PlanId);
            await _progressStore.SetProgressAsync(jobId, progress, ProgressRetention, cancellationToken)
                .ConfigureAwait(false);

            if (_jobQueue != null)
            {
                await _jobQueue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Rollback the just-created job to prevent stranded Queued records
            // that no reconciler will repair (reconciler skips Queued status).
            try
            {
                var failedJob = jobRecord with
                {
                    Status = ExecutionJobStatus.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "Submission failed: progress or queue persistence error.",
                    CurrentPhase = "Failed (submission)"
                };
                await jobStore.TrySetAsync(failedJob, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort rollback; job TTL or manual intervention will repair.
            }

            throw;
        }

        GeoprocessingServiceLog.JobSubmitted(_logger, jobId, plan.PlanId);

        return jobRecord;
    }

    public async Task<ExecutionJobRecord> GetJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(principal, OperatorResourceType.Job, OperatorOperation.Read);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new GeoprocessingValidationException("Job identifier is required.");
        }

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");
        }

        GeoprocessingServiceLog.JobRetrieved(_logger, jobId, job.Status.ToString());
        return job;
    }

    public async Task<AnalysisResultPackage> GetJobResultsAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(principal, OperatorResourceType.Job, OperatorOperation.Read);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new GeoprocessingValidationException("Job identifier is required.");
        }

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");
        }

        if (!IsTerminal(job.Status))
        {
            throw new GeoprocessingPreconditionFailedException(
                $"Job '{jobId}' has not reached a terminal state (current: {job.Status}).");
        }

        GeoprocessingServiceLog.JobResultsUnavailable(_logger, jobId);

        throw new GeoprocessingNotFoundException(
            $"Result package for job '{jobId}' is not yet available. " +
            "Result storage will be implemented with the execution engine.");
    }

    public async Task CancelJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new GeoprocessingValidationException("Job identifier is required.");
        }

        EnsureAuthorized(principal, OperatorResourceType.Job, OperatorOperation.Execute);

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");
        }

        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            if (_jobQueue != null)
            {
                try
                {
                    await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GeoprocessingServiceLog.QueueRemovalFailed(_logger, jobId, ex);
                }
            }

            var staleProgress = await _progressStore.GetProgressAsync<GeoprocessingProgress>(
                jobId, cancellationToken).ConfigureAwait(false);
            if (staleProgress != null && staleProgress.Status != OperationStatus.Cancelled)
            {
                var reconciledProgress = staleProgress.WithCancellation(DateTimeOffset.UtcNow, "Cancelled");
                await _progressStore.SetProgressAsync(
                    jobId, reconciledProgress, ProgressRetention, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (IsTerminal(job.Status))
        {
            GeoprocessingServiceLog.CancelRejectedTerminal(_logger, jobId, job.Status.ToString());
            throw new GeoprocessingPreconditionFailedException(
                $"Job '{jobId}' is in terminal state '{job.Status}' and cannot be cancelled.");
        }

        // Cancelling a running job is a destructive action — require approval.
        // Evaluated after state checks so idempotent and terminal paths remain reachable.
        var approval = _approvalEvaluator.Evaluate(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Execute,
                IsDestructive = true
            });

        if (approval.IsRequired)
        {
            GeoprocessingServiceLog.CancelRejectedApprovalRequired(_logger, approval.PolicyRef ?? "unknown");
            throw new GeoprocessingApprovalRequiredException(
                approval.PolicyRef ?? "unknown",
                "Job cancellation requires approval.");
        }

        var workerOwnsTerminalState = _cancellationNotifiers.CancelAny(jobId);

        if (workerOwnsTerminalState)
        {
            GeoprocessingServiceLog.JobCancellationDelegated(_logger, jobId);
            return;
        }

        var latest = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (latest == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException(
                $"Job '{jobId}' was not found on re-read and could not be cancelled.");
        }

        if (IsTerminal(latest.Status))
        {
            if (latest.Status == ExecutionJobStatus.Cancelled)
            {
                if (_jobQueue != null)
                {
                    try
                    {
                        await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        GeoprocessingServiceLog.QueueRemovalFailed(_logger, jobId, ex);
                    }
                }

                var staleProgress = await _progressStore.GetProgressAsync<GeoprocessingProgress>(
                    jobId, cancellationToken).ConfigureAwait(false);
                if (staleProgress != null && staleProgress.Status != OperationStatus.Cancelled)
                {
                    var reconciledProgress = staleProgress.WithCancellation(DateTimeOffset.UtcNow, "Cancelled");
                    await _progressStore.SetProgressAsync(
                        jobId, reconciledProgress, ProgressRetention, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            GeoprocessingServiceLog.CancelRejectedTerminal(_logger, jobId, latest.Status.ToString());
            throw new GeoprocessingPreconditionFailedException(
                $"Job '{jobId}' is in terminal state '{latest.Status}' and cannot be cancelled.");
        }

        var cancelOutcome = await ExecutionJobCancellationHelper.TryApplyAsync(
            jobStore,
            jobId,
            latest,
            "Cancelled",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        switch (cancelOutcome.State)
        {
            case ExecutionJobCancellationState.CancellationRequested:
                GeoprocessingServiceLog.JobCancellationDelegated(_logger, jobId);
                return;
            case ExecutionJobCancellationState.TerminalConflict:
                GeoprocessingServiceLog.CancelRejectedTerminal(_logger, jobId, cancelOutcome.Job!.Status.ToString());
                throw new GeoprocessingPreconditionFailedException(
                    $"Job '{jobId}' reached terminal state '{cancelOutcome.Job.Status}' before cancellation could be applied.");
            case ExecutionJobCancellationState.Missing:
                GeoprocessingServiceLog.JobNotFound(_logger, jobId);
                throw new GeoprocessingNotFoundException(
                    $"Job '{jobId}' was deleted during cancellation.");
            case ExecutionJobCancellationState.Unconfirmed:
                throw new GeoprocessingPreconditionFailedException(
                    $"Job '{jobId}' cancellation could not be confirmed after retries.");
            case ExecutionJobCancellationState.Cancelled:
                break;
            default:
                throw new InvalidOperationException($"Unexpected durable cancellation outcome '{cancelOutcome.State}'.");
        }

        var now = DateTimeOffset.UtcNow;

        if (_jobQueue != null)
        {
            try
            {
                await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GeoprocessingServiceLog.QueueRemovalFailed(_logger, jobId, ex);
            }
        }

        var progress = await _progressStore.GetProgressAsync<GeoprocessingProgress>(
            jobId, cancellationToken).ConfigureAwait(false);
        if (progress != null)
        {
            var cancelledProgress = progress.WithCancellation(now, "Cancelled");
            await _progressStore.SetProgressAsync(
                jobId, cancelledProgress, ProgressRetention, cancellationToken).ConfigureAwait(false);
        }

        GeoprocessingServiceLog.JobCancelled(_logger, jobId);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void EnsureAuthorized(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation)
    {
        var decision = _authEvaluator.Evaluate(principal, new OperatorAuthorizationRequest
        {
            ResourceType = resourceType,
            Operation = operation
        });

        if (decision.IsAllowed)
        {
            return;
        }

        GeoprocessingServiceLog.AuthorizationDenied(_logger, resourceType.ToString(), operation.ToString());
        throw new GeoprocessingAuthorizationException(decision.RequiresAuthentication);
    }

    private void EnsureApproved(ClaimsPrincipal principal)
    {
        var approval = _approvalEvaluator.Evaluate(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Process,
                Operation = OperatorOperation.Execute
            });

        if (!approval.IsRequired)
        {
            return;
        }

        GeoprocessingServiceLog.SubmitRejectedApprovalRequired(_logger, approval.PolicyRef ?? "unknown");
        throw new GeoprocessingApprovalRequiredException(approval.PolicyRef ?? "unknown");
    }

    private IExecutionJobStore RequireJobStore()
        => _jobStore ?? throw new GeoprocessingStoreUnavailableException();

    private static void ValidatePlanStructure(AnalysisPlan plan)
    {
        // Domain-level structural validation; proto-level validation is handled
        // by the conversion layer (invalid enum values throw during ToDomainPlan).
    }

    private static void EnsurePlanExecutable(AnalysisPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            throw new GeoprocessingValidationException(
                "Plan identifier is required for job submission.");
        }

        if (plan.Steps.Count == 0)
        {
            throw new GeoprocessingValidationException(
                "Plan must contain at least one step for job submission.");
        }
    }

    internal static string CreateJobId(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return $"gp-{Guid.NewGuid():N}";
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey.Trim()));
        return $"gp-{Convert.ToHexString(hashBytes.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    internal static string CreateRequestFingerprint(AnalysisPlan plan)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("planId", plan.PlanId);
            writer.WriteString("intentId", plan.IntentId);

            writer.WriteStartArray("steps");
            foreach (var step in plan.Steps)
            {
                writer.WriteStartObject();
                writer.WriteString("stepId", step.StepId);
                writer.WriteString("kind", step.Kind.ToString());
                writer.WriteString("processId", step.ProcessId ?? "");

                writer.WriteStartArray("inputs");
                foreach (var kv in step.Inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("Key", kv.Key);
                    writer.WriteString("Value", kv.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("dependsOn");
                foreach (var d in step.DependsOn.OrderBy(d => d, StringComparer.Ordinal))
                {
                    writer.WriteStringValue(d);
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("outputs");
            foreach (var o in plan.Outputs.Select(o => o.ToString()).OrderBy(o => o, StringComparer.Ordinal))
            {
                writer.WriteStringValue(o);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void EnsureMatchingIdempotentRequest(ExecutionJobRecord existing, string requestFingerprint)
    {
        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint) &&
            string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new GeoprocessingIdempotencyConflictException();
    }

    private static void EnsureSubmissionDidNotRollback(ExecutionJobRecord existing)
    {
        if (existing.Status == ExecutionJobStatus.Failed
            && string.Equals(existing.CurrentPhase, "Failed (submission)", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Job '{existing.OperationId}' submission previously failed before queueing. Retry with a new idempotency key.");
        }
    }

    internal static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;
}
