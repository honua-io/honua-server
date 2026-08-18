// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Grpc.Core;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Infrastructure.Licensing;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Spec;

/// <summary>
/// gRPC adapter for the spec plan / apply engine. Uses the same
/// <see cref="ISpecPlanner"/> and <see cref="ISpecApplyEngine"/> instances as
/// the REST surface — the event sequence shape is identical.
/// </summary>
internal sealed class HonuaSpecService : Proto.SpecService.SpecServiceBase
{
    private readonly ISpecPlanner _planner;
    private readonly ISpecApplyEngine _applyEngine;
    private readonly IServiceProvider _services;

    public HonuaSpecService(ISpecPlanner planner, ISpecApplyEngine applyEngine, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(applyEngine);
        ArgumentNullException.ThrowIfNull(services);
        _planner = planner;
        _applyEngine = applyEngine;
        _services = services;
    }

    public override async Task<Proto.PlanSpecResponse> PlanSpec(
        Proto.PlanSpecRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Document is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "document is required"));
        }

        CanonicalSpecDocument document;
        try
        {
            document = SpecProtoMapping.FromProto(request.Document);
        }
        catch (SpecDocumentInvalidException invalid)
        {
            var primary = invalid.PrimaryDiagnostic;
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"{primary.Code}: {primary.Message}"));
        }

        var plan = await _planner.PlanAsync(document, context.CancellationToken).ConfigureAwait(false);

        // Mirror the REST handler: fatal plan diagnostics (cycles, duplicate
        // ids, unresolved references, version-skew) must surface as
        // InvalidArgument rather than a successful response with an empty plan
        // plus error warnings. Admin tooling keys off the diagnostic `code`.
        var firstErrorWarning = plan.Warnings.FirstOrDefault(warning => warning.Severity == SpecDiagnosticSeverity.Error);
        if (firstErrorWarning is not null)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"{firstErrorWarning.Code}: {firstErrorWarning.Message}"));
        }

        return new Proto.PlanSpecResponse
        {
            Plan = SpecProtoMapping.ToProto(plan)
        };
    }

    public override async Task ApplySpec(
        Proto.ApplySpecRequest request,
        IServerStreamWriter<Proto.ApplySpecEvent> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);

        if (request.Document is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "document is required"));
        }

        CanonicalSpecDocument document;
        try
        {
            document = SpecProtoMapping.FromProto(request.Document);
        }
        catch (SpecDocumentInvalidException invalid)
        {
            var primary = invalid.PrimaryDiagnostic;
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"{primary.Code}: {primary.Message}"));
        }

        // Proto enums are wire-compatible with any int32, so a forward-rolled
        // client could send (SpecCacheMode)999 that would otherwise hit the
        // default arm in FromProto and silently flow through as ReadWrite.
        // Mirror the kind whitelist and reject with InvalidArgument before
        // building options.
        if (!SpecProtoMapping.IsDefinedCacheMode(request.CacheMode))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"{SpecDiagnosticCodes.UnknownCacheMode}: cache_mode '{((int)request.CacheMode).ToString(CultureInfo.InvariantCulture)}' is not a defined SpecCacheMode value."));
        }

        var plan = await _planner.PlanAsync(document, context.CancellationToken).ConfigureAwait(false);
        var firstErrorWarning = plan.Warnings.FirstOrDefault(warning => warning.Severity == SpecDiagnosticSeverity.Error);
        if (firstErrorWarning is not null)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"{firstErrorWarning.Code}: {firstErrorWarning.Message}"));
        }

        var requestServices = context.GetHttpContext()?.RequestServices ?? _services;
        if (!LicenseGate.IsEntitlementActive(requestServices, FeatureCatalog.AiSpecApplyKey))
        {
            throw LicenseGate.CreateFailedPreconditionRpcException(requestServices, FeatureCatalog.AiSpecApplyKey);
        }

        var options = new SpecApplyOptions
        {
            CacheMode = SpecProtoMapping.FromProto(request.CacheMode),
            // Clamp client-controlled parallelism to the same server-side ceiling
            // as the REST adapter; the orchestrator only enforces the lower bound.
            MaxConcurrency = request.MaxConcurrency > 0
                ? Math.Min((int)request.MaxConcurrency, SpecEndpoints.MaxApplyConcurrency)
                : 4
        };

        SpecApplyHandle handle;
        try
        {
            handle = await _applyEngine.StartAsync(document, options, context.CancellationToken).ConfigureAwait(false);
        }
        catch (SpecDocumentInvalidException invalid)
        {
            var primary = invalid.PrimaryDiagnostic;
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"{primary.Code}: {primary.Message}"));
        }

        // Surface the apply run's id as initial response metadata so clients can
        // correlate with CancelApply (gRPC) or /v1/spec/cancel (REST) while the
        // stream is still active. gRPC trailers are only delivered when the
        // RPC closes, so a client that keyed off trailers could not cancel an
        // in-flight run — initial metadata is flushed before the first event.
        //
        // Geospatial.Grpc 0.2.0-alpha.1 renamed the in-band carrier of this value
        // (ApplySpecEvent.apply_token -> ApplySpecEvent.job_id, CancelApplyRequest.apply_token
        // -> CancelJobRequest.job_id). The metadata key is a Honua-side header, not part of the
        // proto, and the REST cancel endpoint still spells it "apply token", so the existing
        // x-spec-apply-token key is kept for continuity; x-spec-job-id is emitted alongside it
        // under the new vocabulary. Both carry the identical value.
        var initialMetadata = new Metadata
        {
            { "x-spec-apply-token", handle.ApplyToken },
            { "x-spec-job-id", handle.ApplyToken }
        };
        await context.WriteResponseHeadersAsync(initialMetadata).ConfigureAwait(false);

        // context.CancellationToken is cancelled when the client disconnects
        // from the stream; this only stops our outbound writes. The apply run
        // itself is backed by an orchestrator-owned CTS and keeps going until
        // an explicit CancelApply (gRPC) or /v1/spec/cancel (REST) trips it.
        await foreach (var evt in handle.Events.WithCancellation(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(SpecProtoMapping.ToProto(evt)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cooperatively cancels an in-flight apply run.
    /// </summary>
    /// <remarks>
    /// Geospatial.Grpc 0.2.0-alpha.1 converged this RPC onto the shared job-control messages:
    /// it now takes <c>CancelJobRequest.job_id</c> (the value previously sent as
    /// <c>apply_token</c>) and returns <c>CancelJobResponse</c>, which carries a
    /// <c>JobState</c> instead of the old <c>bool cancelled</c>. There is no boolean left to
    /// carry "no such run", so an accepted cancellation reports
    /// <c>JOB_STATE_CANCELLED</c> and an unknown or already-terminal run reports
    /// <c>JOB_STATE_UNSPECIFIED</c> — the server declines to assert a state for a run it does
    /// not hold. The call stays non-faulting either way, matching the previous
    /// <c>cancelled = false</c> behaviour rather than turning a no-op cancel into a NotFound.
    /// </remarks>
    public override Task<Proto.CancelJobResponse> CancelApply(
        Proto.CancelJobRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.JobId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "job_id is required"));
        }

        var cancelled = _applyEngine.TryCancel(request.JobId);
        return Task.FromResult(new Proto.CancelJobResponse
        {
            JobId = request.JobId,
            State = cancelled ? Proto.JobState.Cancelled : Proto.JobState.Unspecified
        });
    }
}
