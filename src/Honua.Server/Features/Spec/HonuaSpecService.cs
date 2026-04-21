// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
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

    public HonuaSpecService(ISpecPlanner planner, ISpecApplyEngine applyEngine)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(applyEngine);
        _planner = planner;
        _applyEngine = applyEngine;
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
        foreach (var warning in plan.Warnings)
        {
            if (warning.Severity == SpecDiagnosticSeverity.Error)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"{warning.Code}: {warning.Message}"));
            }
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

        var options = new SpecApplyOptions
        {
            CacheMode = SpecProtoMapping.FromProto(request.CacheMode),
            MaxConcurrency = request.MaxConcurrency > 0 ? (int)request.MaxConcurrency : 4
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

        // Surface the apply token via trailers so clients can correlate
        // with /v1/spec/cancel (REST) or CancelApply (gRPC).
        context.ResponseTrailers.Add("x-spec-apply-token", handle.ApplyToken);

        // context.CancellationToken is cancelled when the client disconnects
        // from the stream; this only stops our outbound writes. The apply run
        // itself is backed by an orchestrator-owned CTS and keeps going until
        // an explicit CancelApply (gRPC) or /v1/spec/cancel (REST) trips it.
        await foreach (var evt in handle.Events.WithCancellation(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(SpecProtoMapping.ToProto(evt)).ConfigureAwait(false);
        }
    }

    public override Task<Proto.CancelApplyResponse> CancelApply(
        Proto.CancelApplyRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.ApplyToken))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "apply_token is required"));
        }

        var cancelled = _applyEngine.TryCancel(request.ApplyToken);
        return Task.FromResult(new Proto.CancelApplyResponse
        {
            Cancelled = cancelled
        });
    }
}
