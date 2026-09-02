// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Grpc.Core;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Backpressure;
using Honua.ServiceDefaults;
using Proto = Geospatial.V1;

namespace Honua.Geoprocessing;

/// <summary>
/// gRPC service implementation for typed geoprocessing execution and job lifecycle management.
/// Thin proto-to-domain translator that delegates to <see cref="IGeoprocessingJobService"/>.
/// </summary>
internal sealed partial class HonuaProcessService : Proto.ProcessService.ProcessServiceBase
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

    public override async Task<Proto.ValidateResponse> ValidatePlan(
        Proto.ValidatePlanRequest request,
        ServerCallContext context)
    {
        EnrichActivity("ValidatePlan");

        try
        {
            await _jobService.EnsureCallerAuthorizedAsync(
                context.GetHttpContext().User,
                OperatorResourceType.Process,
                OperatorOperation.Read,
                context.CancellationToken).ConfigureAwait(false);

            ValidateProtoStructure(request.Plan);
            var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(request.Plan);
            var result = _jobService.ValidatePlan(domainPlan, context.GetHttpContext().User);
            return GeoprocessingConversionHelpers.ToProtoValidateResponse(result);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex, context);
        }
    }

    public override async Task<Proto.DryRunResponse> DryRunPlan(
        Proto.DryRunPlanRequest request,
        ServerCallContext context)
    {
        EnrichActivity("DryRunPlan");

        try
        {
            await _jobService.EnsureCallerAuthorizedAsync(
                context.GetHttpContext().User,
                OperatorResourceType.Process,
                OperatorOperation.Read,
                context.CancellationToken).ConfigureAwait(false);

            ValidateProtoStructure(request.Plan);
            var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(request.Plan);
            var validation = _jobService.ValidatePlan(domainPlan, context.GetHttpContext().User);
            if (!validation.IsExecutable)
            {
                return GeoprocessingConversionHelpers.ToProtoDryRunResponse(validation);
            }

            var result = _jobService.DryRunPlan(domainPlan, context.GetHttpContext().User);
            return GeoprocessingConversionHelpers.ToProtoDryRunResponse(result);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex, context);
        }
    }

    public override async Task<Proto.ExecutePlanResponse> ExecutePlan(
        Proto.ExecutePlanRequest request,
        ServerCallContext context)
    {
        EnrichActivity("ExecutePlan");

        try
        {
            await _jobService.EnsureCallerAuthorizedAsync(
                context.GetHttpContext().User,
                OperatorResourceType.Process,
                OperatorOperation.Execute,
                context.CancellationToken).ConfigureAwait(false);

            throw new RpcException(new Status(
                StatusCode.Unimplemented,
                "Synchronous plan execution is not yet available. Use SubmitJob for asynchronous execution."));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex, context);
        }
    }

    public override async Task ExecutePlanStream(
        Proto.ExecutePlanRequest request,
        IServerStreamWriter<Proto.ExecutionEvent> responseStream,
        ServerCallContext context)
    {
        EnrichActivity("ExecutePlanStream");

        try
        {
            await _jobService.EnsureCallerAuthorizedAsync(
                context.GetHttpContext().User,
                OperatorResourceType.Process,
                OperatorOperation.Execute,
                context.CancellationToken).ConfigureAwait(false);

            throw new RpcException(new Status(
                StatusCode.Unimplemented,
                "Streaming plan execution is not yet available. Use SubmitJob for asynchronous execution."));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex, context);
        }
    }

    public override async Task<Proto.SubmitJobResponse> SubmitJob(
        Proto.SubmitJobRequest request,
        ServerCallContext context)
    {
        EnrichActivity("SubmitJob");

        try
        {
            await _jobService.EnsureCallerAuthorizedAsync(
                context.GetHttpContext().User,
                OperatorResourceType.Process,
                OperatorOperation.Execute,
                context.CancellationToken).ConfigureAwait(false);

            ValidateProtoStructure(request.Plan);
            var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(request.Plan);
            var idempotencyKey = ResolveIdempotencyKey(request.Context);
            var jobRecord = await _jobService.SubmitJobAsync(
                domainPlan, idempotencyKey,
                context.GetHttpContext().User, null, context.CancellationToken).ConfigureAwait(false);
            return GeoprocessingConversionHelpers.ToProtoSubmitJobResponse(jobRecord);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex, context);
        }
    }

    public override async Task<Proto.GetJobResponse> GetJob(
        Proto.GetJobRequest request,
        ServerCallContext context)
    {
        EnrichActivity("GetJob");

        try
        {
            var job = await _jobService.GetJobAsync(
                request.JobId, context.GetHttpContext().User, context.CancellationToken).ConfigureAwait(false);
            return GeoprocessingConversionHelpers.ToProtoGetJobResponse(job);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex, context);
        }
    }

    public override async Task<Proto.GetJobResultResponse> GetJobResult(
        Proto.GetJobResultRequest request,
        ServerCallContext context)
    {
        EnrichActivity("GetJobResult");

        try
        {
            var results = await _jobService.GetJobResultsAsync(
                request.JobId, context.GetHttpContext().User, context.CancellationToken).ConfigureAwait(false);
            return GeoprocessingConversionHelpers.ToProtoGetJobResultResponse(request.JobId, results);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex, context);
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
            return new Proto.CancelJobResponse { JobId = request.JobId, State = Proto.JobState.Cancelled };
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw MapToRpcException(ex, context);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void ValidateProtoStructure(Proto.ExecutionPlan? plan)
    {
        if (plan == null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Execution plan is required."));
        }

        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Kind))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Step '{step.StepId}' has unsupported step kind '{step.Kind}'."));
            }
        }

        foreach (var output in plan.ExpectedOutputs)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Unsupported artifact kind '{output}'."));
            }
        }
    }

    private static string? ResolveIdempotencyKey(Proto.ExecutionContext? executionContext)
    {
        if (executionContext is not null
            && executionContext.Metadata.TryGetValue("idempotency_key", out var snakeCaseKey)
            && !string.IsNullOrWhiteSpace(snakeCaseKey))
        {
            return snakeCaseKey;
        }

        if (executionContext is not null
            && executionContext.Metadata.TryGetValue("idempotencyKey", out var camelCaseKey)
            && !string.IsNullOrWhiteSpace(camelCaseKey))
        {
            return camelCaseKey;
        }

        return null;
    }

    private RpcException MapToRpcException(Exception ex, ServerCallContext context)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return new RpcException(new Status(StatusCode.Cancelled, "The operation was cancelled."));

            case TimeoutException timeoutEx:
                Log.UnexpectedTimeout(_logger, timeoutEx);
                return new RpcException(new Status(StatusCode.DeadlineExceeded, "The operation timed out."));

            case GeoprocessingAuthorizationException authEx:
                return new RpcException(new Status(
                    authEx.RequiresAuthentication ? StatusCode.Unauthenticated : StatusCode.PermissionDenied,
                    authEx.Message));

            case GeoprocessingApprovalRequiredException approvalEx:
                return new RpcException(new Status(StatusCode.FailedPrecondition, approvalEx.Message));

            case GeoprocessingNotFoundException notFoundEx:
                return new RpcException(new Status(StatusCode.NotFound, notFoundEx.Message));

            case GeoprocessingPreconditionFailedException preconditionEx:
                return new RpcException(new Status(StatusCode.FailedPrecondition, preconditionEx.Message));

            case GeoprocessingValidationException validationEx:
                return new RpcException(new Status(StatusCode.InvalidArgument, validationEx.Message));

            // honua-release#202: carry the capability-unavailable receipt in trailing metadata so a
            // gRPC client can branch on the same fields the HTTP surfaces expose, instead of
            // string-matching the status detail. Mirrors the admission-trailer pattern below.
            case GeoprocessingStoreUnavailableException storeEx:
                return storeEx.HasDependencyReceipt
                    ? new RpcException(
                        new Status(StatusCode.Unavailable, storeEx.Message),
                        BuildCapabilityUnavailableTrailers(storeEx))
                    : new RpcException(new Status(StatusCode.Unavailable, storeEx.Message));

            case GeoprocessingIdempotencyConflictException conflictEx:
                return new RpcException(new Status(StatusCode.AlreadyExists, conflictEx.Message));

            case GeoprocessingAdmissionException admissionEx:
                return new RpcException(
                    new Status(StatusCode.ResourceExhausted, admissionEx.Message),
                    new global::Grpc.Core.Metadata
                    {
                        { "honua-admission-outcome", admissionEx.Outcome.ToString() },
                        { "honua-admission-dimension", admissionEx.DenyingDimension.ToString() },
                        { "honua-admission-policy-ref", admissionEx.PolicyRef },
                        { BackpressureMetadata.ErrorCodeKey, BackpressureMetadata.RateLimitExceededCode },
                        { BackpressureMetadata.RetryableKey, "true" },
                        { BackpressureMetadata.RetryAfterKey, admissionEx.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        { BackpressureMetadata.CorrelationIdKey, context.GetHttpContext().TraceIdentifier },
                    });

            case InvalidOperationException opEx:
                Log.UnexpectedInternalError(_logger, opEx);
                return new RpcException(new Status(StatusCode.Internal, "An unexpected error occurred."));

            default:
                Log.UnexpectedInternalError(_logger, ex);
                return new RpcException(new Status(StatusCode.Internal, "An unexpected error occurred."));
        }
    }

    /// <summary>
    /// Projects the capability-unavailable receipt (honua-release#202) onto gRPC trailing
    /// metadata. gRPC has no problem+json extension members, so the fields ride trailers under the
    /// same <c>honua-</c> prefix the admission path already uses; a client reads them without
    /// parsing the status detail. Keys are lower-case because gRPC metadata keys are
    /// case-insensitive and normalised to lower-case on the wire.
    /// </summary>
    private static global::Grpc.Core.Metadata BuildCapabilityUnavailableTrailers(
        GeoprocessingStoreUnavailableException exception)
    {
        var trailers = new global::Grpc.Core.Metadata
        {
            { "honua-error-code", exception.ErrorCode! },
            { "honua-capability", exception.CapabilityId! },
            { "honua-remediation", exception.Remediation! },
            { "honua-remediation-ref", exception.RemediationRef! },
        };

        if (exception.MissingDependency is not null)
        {
            trailers.Add("honua-missing-dependency", exception.MissingDependency);
        }

        if (exception.MissingEntitlement is not null)
        {
            trailers.Add("honua-missing-entitlement", exception.MissingEntitlement);
        }

        return trailers;
    }

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

    private static partial class Log
    {
        [LoggerMessage(7900, LogLevel.Error,
            "Unexpected internal error in geoprocessing gRPC service")]
        public static partial void UnexpectedInternalError(ILogger logger, Exception exception);

        [LoggerMessage(7901, LogLevel.Warning,
            "Timeout in geoprocessing gRPC service")]
        public static partial void UnexpectedTimeout(ILogger logger, Exception exception);
    }
}
