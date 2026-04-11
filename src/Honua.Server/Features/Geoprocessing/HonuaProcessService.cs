// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
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
    private readonly IExecutionJobStore _jobStore;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IJobCancellationNotifier _cancellationNotifier;
    private readonly IOperatorAuthorizationEvaluator _authEvaluator;
    private readonly IOperatorApprovalEvaluator _approvalEvaluator;
    private readonly ILogger<HonuaProcessService> _logger;

    [ActivatorUtilitiesConstructor]
    public HonuaProcessService(
        IExecutionJobStore jobStore,
        IUniversalProgressStore progressStore,
        IJobCancellationNotifier cancellationNotifier,
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        ILogger<HonuaProcessService> logger)
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
        : this(jobStore, progressStore, cancellationNotifier, authEvaluator, approvalEvaluator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HonuaProcessService>.Instance)
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

        var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(plan);
        var now = DateTimeOffset.UtcNow;
        var jobId = $"gp-{Guid.NewGuid():N}";

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? null
                    : request.IdempotencyKey,
                RequestedBy = context.GetHttpContext().User.Identity?.Name
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = $"geoprocessing:{domainPlan.PlanId}"
            }
        };

        await _jobStore.TryCreateAsync(jobRecord, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        var progress = GeoprocessingProgress.CreateInitial(jobId);
        await _progressStore.SetProgressAsync(jobId, progress, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        GeoprocessingServiceLog.JobSubmitted(_logger, jobId, domainPlan.PlanId);

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

        var job = await _jobStore.GetAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
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

        var job = await _jobStore.GetAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
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

        GeoprocessingServiceLog.JobResultsRetrieved(_logger, request.JobId);

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

        var job = await _jobStore.GetAsync(request.JobId, context.CancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, request.JobId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Job '{request.JobId}' not found."));
        }

        _cancellationNotifier.Cancel(request.JobId);

        var now = DateTimeOffset.UtcNow;
        var cancelled = job with
        {
            Status = ExecutionJobStatus.Cancelled,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Cancelled"
        };

        await _jobStore.SetAsync(cancelled, cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var progress = await _progressStore.GetProgressAsync<GeoprocessingProgress>(
            request.JobId, context.CancellationToken).ConfigureAwait(false);
        if (progress != null)
        {
            var cancelledProgress = progress.WithCancellation(now, "Cancelled");
            await _progressStore.SetProgressAsync(
                request.JobId, cancelledProgress, cancellationToken: context.CancellationToken).ConfigureAwait(false);
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

    private static void ValidatePlanStructure(Proto.AnalysisPlan? plan)
    {
        if (plan == null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Analysis plan is required."));
        }
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
