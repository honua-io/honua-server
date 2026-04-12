// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// gRPC service implementation for typed geoprocessing execution and job lifecycle management.
/// </summary>
internal sealed class HonuaProcessService : Proto.ProcessService.ProcessServiceBase
{
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(7);

    private readonly IExecutionJobStore? _jobStore;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IJobCancellationNotifier _cancellationNotifier;
    private readonly IOperatorAuthorizationEvaluator _authEvaluator;
    private readonly IOperatorApprovalEvaluator _approvalEvaluator;
    private readonly ILogger<HonuaProcessService> _logger;

    [ActivatorUtilitiesConstructor]
    public HonuaProcessService(
        IUniversalProgressStore progressStore,
        IJobCancellationNotifier cancellationNotifier,
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        ILogger<HonuaProcessService> logger,
        IExecutionJobStore? jobStore = null)
    {
        _jobStore = jobStore;
        _progressStore = progressStore;
        _cancellationNotifier = cancellationNotifier;
        _authEvaluator = authEvaluator;
        _approvalEvaluator = approvalEvaluator;
        _logger = logger;
    }

    public HonuaProcessService(
        IExecutionJobStore jobStore,
        IUniversalProgressStore progressStore,
        IJobCancellationNotifier cancellationNotifier,
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator)
        : this(progressStore, cancellationNotifier, authEvaluator, approvalEvaluator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HonuaProcessService>.Instance, jobStore)
    {
    }

    public override Task<Proto.ValidatePlanResponse> ValidatePlan(
        Proto.ValidatePlanRequest request,
        ServerCallContext context)
    {
        EnrichActivity("ValidatePlan");
        EnsureAuthorized(context, OperatorResourceType.Process, OperatorOperation.Read);

        var plan = request.Plan;
        ValidatePlanStructure(plan);

        var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(plan);

        var violations = new List<GeoprocessingValidationFailure>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(domainPlan.PlanId))
        {
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "EMPTY_PLAN_ID",
                Message = "Plan identifier is required.",
                FieldPath = "plan_id"
            });
        }

        if (domainPlan.Steps.Count == 0)
        {
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "EMPTY_STEPS",
                Message = "Plan must contain at least one step.",
                FieldPath = "steps"
            });
        }

        var approvalReq = _approvalEvaluator.Evaluate(
            context.GetHttpContext().User,
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

        GeoprocessingServiceLog.PlanValidated(_logger, domainPlan.PlanId ?? "", result.IsExecutable, violations.Count);

        return Task.FromResult(GeoprocessingConversionHelpers.ToProtoValidatePlanResponse(result));
    }

    public override Task<Proto.DryRunPlanResponse> DryRunPlan(
        Proto.DryRunPlanRequest request,
        ServerCallContext context)
    {
        EnrichActivity("DryRunPlan");
        EnsureAuthorized(context, OperatorResourceType.Process, OperatorOperation.Read);

        var plan = request.Plan;
        ValidatePlanStructure(plan);

        var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(plan);

        var result = new DryRunResult
        {
            EstimatedDurationSeconds = 0,
            EstimatedArtifacts = domainPlan.Outputs,
            SideEffects = []
        };

        GeoprocessingServiceLog.DryRunCompleted(_logger, domainPlan.PlanId, result.EstimatedDurationSeconds);

        return Task.FromResult(GeoprocessingConversionHelpers.ToProtoDryRunPlanResponse(result));
    }

    public override Task<Proto.ExecutePlanResponse> ExecutePlan(
        Proto.ExecutePlanRequest request,
        ServerCallContext context)
    {
        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "Synchronous plan execution is not yet available. Use SubmitPlanJob for asynchronous execution."));
    }

    public override async Task<Proto.ExecutionJob> SubmitPlanJob(
        Proto.SubmitPlanJobRequest request,
        ServerCallContext context)
    {
        EnrichActivity("SubmitPlanJob");
        EnsureAuthorized(context, OperatorResourceType.Process, OperatorOperation.Execute);

        var plan = request.Plan;
        ValidatePlanStructure(plan);
        EnsureApproved(context);

        var jobStore = RequireJobStore();
        var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(plan);
        var now = DateTimeOffset.UtcNow;
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey;
        var jobId = CreateJobId(idempotencyKey);
        var requestFingerprint = CreateRequestFingerprint(domainPlan);

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                IdempotencyKey = idempotencyKey,
                RequestedBy = context.GetHttpContext().User.Identity?.Name,
                RequestFingerprint = requestFingerprint
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = $"geoprocessing:{domainPlan.PlanId}"
            }
        };

        var created = await jobStore.TryCreateAsync(jobRecord, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        if (!created)
        {
            var existing = await jobStore.GetAsync(jobId, context.CancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                EnsureMatchingIdempotentRequest(existing, requestFingerprint);
                GeoprocessingServiceLog.JobSubmittedIdempotent(_logger, jobId);
                return GeoprocessingConversionHelpers.ToProtoExecutionJob(existing);
            }

            throw new RpcException(new Status(StatusCode.Internal,
                "Failed to create or locate execution job."));
        }

        var progress = GeoprocessingProgress.CreateForSubmittedJob(jobId, domainPlan.PlanId);
        await _progressStore.SetProgressAsync(jobId, progress, ProgressRetention, context.CancellationToken)
            .ConfigureAwait(false);

        GeoprocessingServiceLog.JobSubmitted(_logger, jobId, domainPlan.PlanId);
        GeoprocessingServiceLog.JobSubmittedStubbed(_logger, jobId);

        return GeoprocessingConversionHelpers.ToProtoExecutionJob(jobRecord);
    }

    public override async Task<Proto.ExecutionJob> GetJob(
        Proto.GetJobRequest request,
        ServerCallContext context)
    {
        EnrichActivity("GetJob");
        EnsureAuthorized(context, OperatorResourceType.Job, OperatorOperation.Read);

        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Job identifier is required."));
        }

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, request.JobId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Job '{request.JobId}' not found."));
        }

        GeoprocessingServiceLog.JobRetrieved(_logger, request.JobId, job.Status.ToString());

        return GeoprocessingConversionHelpers.ToProtoExecutionJob(job);
    }

    public override async Task<Proto.AnalysisResultPackage> GetJobResults(
        Proto.GetJobResultsRequest request,
        ServerCallContext context)
    {
        EnrichActivity("GetJobResults");
        EnsureAuthorized(context, OperatorResourceType.Job, OperatorOperation.Read);

        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Job identifier is required."));
        }

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, request.JobId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Job '{request.JobId}' not found."));
        }

        if (!IsTerminal(job.Status))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Job '{request.JobId}' has not reached a terminal state (current: {job.Status})."));
        }

        GeoprocessingServiceLog.JobResultsUnavailable(_logger, request.JobId);

        throw new RpcException(new Status(
            StatusCode.NotFound,
            $"Result package for job '{request.JobId}' is not yet available. " +
            "Result storage will be implemented with the execution engine."));
    }

    public override async Task<Proto.CancelJobResponse> CancelJob(
        Proto.CancelJobRequest request,
        ServerCallContext context)
    {
        EnrichActivity("CancelJob");
        EnsureAuthorized(context, OperatorResourceType.Job, OperatorOperation.Execute);

        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Job identifier is required."));
        }

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, request.JobId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Job '{request.JobId}' not found."));
        }

        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            return new Proto.CancelJobResponse();
        }

        if (IsTerminal(job.Status))
        {
            GeoprocessingServiceLog.CancelRejectedTerminal(_logger, request.JobId, job.Status.ToString());
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Job '{request.JobId}' is in terminal state '{job.Status}' and cannot be cancelled."));
        }

        var workerOwnsTerminalState = _cancellationNotifier.Cancel(request.JobId);

        if (workerOwnsTerminalState)
        {
            GeoprocessingServiceLog.JobCancellationDelegated(_logger, request.JobId);
            return new Proto.CancelJobResponse();
        }

        var latest = await jobStore.GetAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
        if (latest != null && IsTerminal(latest.Status))
        {
            return new Proto.CancelJobResponse();
        }

        var now = DateTimeOffset.UtcNow;
        var cancelled = (latest ?? job) with
        {
            Status = ExecutionJobStatus.Cancelled,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Cancelled"
        };

        await jobStore.SetAsync(cancelled, cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var progress = await _progressStore.GetProgressAsync<GeoprocessingProgress>(
            request.JobId, context.CancellationToken).ConfigureAwait(false);
        if (progress != null)
        {
            var cancelledProgress = progress.WithCancellation(now, "Cancelled");
            await _progressStore.SetProgressAsync(
                request.JobId, cancelledProgress, ProgressRetention, context.CancellationToken).ConfigureAwait(false);
        }

        GeoprocessingServiceLog.JobCancelled(_logger, request.JobId);

        return new Proto.CancelJobResponse();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void EnsureAuthorized(
        ServerCallContext context,
        OperatorResourceType resourceType,
        OperatorOperation operation)
    {
        var httpContext = context.GetHttpContext();
        var decision = _authEvaluator.Evaluate(httpContext.User, new OperatorAuthorizationRequest
        {
            ResourceType = resourceType,
            Operation = operation
        });

        if (decision.IsAllowed)
        {
            return;
        }

        GeoprocessingServiceLog.AuthorizationDenied(_logger, resourceType.ToString(), operation.ToString());

        throw new RpcException(new Status(
            decision.RequiresAuthentication ? StatusCode.Unauthenticated : StatusCode.PermissionDenied,
            decision.RequiresAuthentication
                ? "Authentication is required for this operation."
                : "You do not have permission to perform this operation."));
    }

    private void EnsureApproved(ServerCallContext context)
    {
        var approval = _approvalEvaluator.Evaluate(
            context.GetHttpContext().User,
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

        throw new RpcException(new Status(
            StatusCode.FailedPrecondition,
            $"This operation requires approval (policy: {approval.PolicyRef}). " +
            "Use ValidatePlan to check approval requirements before submission."));
    }

    private IExecutionJobStore RequireJobStore()
        => _jobStore ?? throw new RpcException(new Status(
            StatusCode.Unavailable,
            "Job operations require Redis-backed durable storage. Ensure a valid Redis connection is configured."));

    private static void ValidatePlanStructure(Proto.AnalysisPlan? plan)
    {
        if (plan == null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Analysis plan is required."));
        }

        foreach (var step in plan.Steps)
        {
            if (step.Kind is Proto.PlanStepKind.Unspecified || !Enum.IsDefined(step.Kind))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Step '{step.StepId}' has unsupported step kind '{step.Kind}'."));
            }
        }

        foreach (var output in plan.Outputs)
        {
            if (output is Proto.ArtifactKind.Unspecified || !Enum.IsDefined(output))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Unsupported artifact kind '{output}'."));
            }
        }
    }

    private static string CreateJobId(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return $"gp-{Guid.NewGuid():N}";
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey.Trim()));
        return $"gp-{Convert.ToHexString(hashBytes.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static string CreateRequestFingerprint(AnalysisPlan plan)
    {
        var normalizedSteps = plan.Steps.Select(step => new
        {
            stepId = step.StepId,
            kind = step.Kind.ToString(),
            processId = step.ProcessId ?? "",
            inputs = step.Inputs
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
                .ToArray(),
            dependsOn = step.DependsOn
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToArray()
        }).ToArray();

        var payload = JsonSerializer.Serialize(new
        {
            planId = plan.PlanId,
            intentId = plan.IntentId,
            steps = normalizedSteps,
            outputs = plan.Outputs
                .Select(o => o.ToString())
                .OrderBy(o => o, StringComparer.Ordinal)
                .ToArray()
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static void EnsureMatchingIdempotentRequest(ExecutionJobRecord existing, string requestFingerprint)
    {
        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint) &&
            string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.AlreadyExists,
            "Idempotency key is already associated with a different request."));
    }

    private static void EnrichActivity(string operation)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        activity.SetTag(HonuaTelemetry.Tags.Protocol, "grpc");
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;
}
