// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Grpc.Core;
using Honua.ServiceDefaults;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// gRPC service implementation for typed geoprocessing execution and job lifecycle management.
/// Thin proto-to-domain translator that delegates to <see cref="IGeoprocessingJobService"/>.
/// </summary>
internal sealed class HonuaProcessService : Proto.ProcessService.ProcessServiceBase
{
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<HonuaProcessService> _logger;

    public HonuaProcessService(
        IGeoprocessingJobService jobService,
        ILogger<HonuaProcessService> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public override Task<Proto.ValidatePlanResponse> ValidatePlan(
        Proto.ValidatePlanRequest request,
        ServerCallContext context)
    {
        EnrichActivity("ValidatePlan");
        ValidateProtoStructure(request.Plan);
        var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(request.Plan);

        try
        {
            var result = _jobService.ValidatePlan(domainPlan, context.GetHttpContext().User);
            return Task.FromResult(GeoprocessingConversionHelpers.ToProtoValidatePlanResponse(result));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex);
        }
    }

    public override Task<Proto.DryRunPlanResponse> DryRunPlan(
        Proto.DryRunPlanRequest request,
        ServerCallContext context)
    {
        EnrichActivity("DryRunPlan");
        ValidateProtoStructure(request.Plan);
        var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(request.Plan);

        try
        {
            var result = _jobService.DryRunPlan(domainPlan, context.GetHttpContext().User);
            return Task.FromResult(GeoprocessingConversionHelpers.ToProtoDryRunPlanResponse(result));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex);
        }
    }

    public override Task<Proto.ExecutePlanResponse> ExecutePlan(
        Proto.ExecutePlanRequest request,
        ServerCallContext context)
    {
        EnrichActivity("ExecutePlan");

        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "Synchronous plan execution is not yet available. Use SubmitPlanJob for asynchronous execution."));
    }

    public override async Task<Proto.ExecutionJob> SubmitPlanJob(
        Proto.SubmitPlanJobRequest request,
        ServerCallContext context)
    {
        EnrichActivity("SubmitPlanJob");
        ValidateProtoStructure(request.Plan);
        var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(request.Plan);

        try
        {
            var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? null
                : request.IdempotencyKey;
            var jobRecord = await _jobService.SubmitJobAsync(
                domainPlan, idempotencyKey,
                context.GetHttpContext().User, null, context.CancellationToken).ConfigureAwait(false);
            return GeoprocessingConversionHelpers.ToProtoExecutionJob(jobRecord);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex);
        }
    }

    public override async Task<Proto.ExecutionJob> GetJob(
        Proto.GetJobRequest request,
        ServerCallContext context)
    {
        EnrichActivity("GetJob");

        try
        {
            var job = await _jobService.GetJobAsync(
                request.JobId, context.GetHttpContext().User, context.CancellationToken).ConfigureAwait(false);
            return GeoprocessingConversionHelpers.ToProtoExecutionJob(job);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex);
        }
    }

    public override async Task<Proto.AnalysisResultPackage> GetJobResults(
        Proto.GetJobResultsRequest request,
        ServerCallContext context)
    {
        EnrichActivity("GetJobResults");

        try
        {
            var results = await _jobService.GetJobResultsAsync(
                request.JobId, context.GetHttpContext().User, context.CancellationToken).ConfigureAwait(false);
            return GeoprocessingConversionHelpers.ToProtoResultPackage(results);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex);
        }
    }

    public override async Task<Proto.CancelJobResponse> CancelJob(
        Proto.CancelJobRequest request,
        ServerCallContext context)
    {
        EnrichActivity("CancelJob");

        try
        {
            await _jobService.CancelJobAsync(
                request.JobId, context.GetHttpContext().User, context.CancellationToken).ConfigureAwait(false);
            return new Proto.CancelJobResponse();
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void ValidateProtoStructure(Proto.AnalysisPlan? plan)
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

    private static RpcException MapToRpcException(Exception ex) => ex switch
    {
        GeoprocessingAuthorizationException authEx => new RpcException(new Status(
            authEx.RequiresAuthentication ? StatusCode.Unauthenticated : StatusCode.PermissionDenied,
            authEx.Message)),

        GeoprocessingApprovalRequiredException approvalEx => new RpcException(new Status(
            StatusCode.FailedPrecondition, approvalEx.Message)),

        GeoprocessingNotFoundException notFoundEx => new RpcException(new Status(
            StatusCode.NotFound, notFoundEx.Message)),

        GeoprocessingPreconditionFailedException preconditionEx => new RpcException(new Status(
            StatusCode.FailedPrecondition, preconditionEx.Message)),

        GeoprocessingValidationException validationEx => new RpcException(new Status(
            StatusCode.InvalidArgument, validationEx.Message)),

        GeoprocessingStoreUnavailableException storeEx => new RpcException(new Status(
            StatusCode.Unavailable, storeEx.Message)),

        GeoprocessingIdempotencyConflictException conflictEx => new RpcException(new Status(
            StatusCode.AlreadyExists, conflictEx.Message)),

        InvalidOperationException opEx => new RpcException(new Status(
            StatusCode.Internal, opEx.Message)),

        _ => new RpcException(new Status(StatusCode.Internal, "An unexpected error occurred."))
    };

    private static void EnrichActivity(string operation)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        activity.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
    }
}
