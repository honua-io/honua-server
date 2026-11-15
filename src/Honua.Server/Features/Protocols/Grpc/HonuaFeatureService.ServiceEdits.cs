// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    public override async Task<Proto.ApplyServiceEditsResponse> ApplyServiceEdits(
        Proto.ApplyServiceEditsRequest request,
        ServerCallContext context)
    {
        EnrichActivity("apply_service_edits", request.ServiceId, -1);
        var edits = await PrepareServiceEditsAsync(request, context).ConfigureAwait(false);
        var receipts = context.GetHttpContext().RequestServices.GetRequiredService<GrpcServiceEditsReceipts>();
        var fingerprint = GrpcServiceEditsReceipts.Fingerprint(request);
        using var keyed = await EnterServiceEditAsync(new ServiceEditCall(request, context, receipts)).ConfigureAwait(false);
        if (keyed?.OutcomeUnknown == true)
        {
            throw OutcomeMayHaveCommitted();
        }
        if (keyed?.Response is { } cached)
        {
            EnsureServiceFingerprint(keyed.RequestFingerprint, fingerprint);
            return FilterServiceGlobalIds(edits, cached);
        }
        for (var index = 0; index < edits.Length; index++)
        {
            edits[index] = await PrepareGrpcTargetsAsync(edits[index]).ConfigureAwait(false);
        }
        return await ExecuteServiceEditsAsync(new ServiceEditExecution(
            new ServiceEditCall(request, context, receipts), edits, keyed) { Fingerprint = fingerprint }).ConfigureAwait(false);
    }

    private async Task<PreparedGrpcEdit[]> PrepareServiceEditsAsync(
        Proto.ApplyServiceEditsRequest request,
        ServerCallContext context)
    {
        if (request.Edits.Count == 0 || request.Edits.Select(edit => edit.LayerId).Distinct().Count() != request.Edits.Count)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Edits must contain distinct layers."));
        }
        var result = new PreparedGrpcEdit[request.Edits.Count];
        var creates = ImmutableArray.CreateBuilder<Feature>();
        var updates = ImmutableArray.CreateBuilder<Feature>();
        var deletes = ImmutableArray.CreateBuilder<long>();
        for (var index = 0; index < request.Edits.Count; index++)
        {
            var edit = ServiceLayerRequest(request.ServiceId, request.Edits[index]);
            var layer = await ValidateGrpcLayerAsync(request.ServiceId, edit.LayerId, context.CancellationToken)
                .ConfigureAwait(false);
            result[index] = await PrepareGrpcEditAsync(new EditCall(edit, layer, context)).ConfigureAwait(false);
            creates.AddRange(result[index].Batch.Creates);
            updates.AddRange(result[index].Batch.Updates);
            deletes.AddRange(result[index].Batch.Deletes);
            if (layer.Snapshot?.ResolveStorageBinding(layer.Publication)?.ConnectionId is not null)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "Service edits require all layers to use the managed transaction provider."));
            }
        }
        EnsureEditLimits(FeatureEditBatch.Create(creates.ToImmutable(), updates.ToImmutable(), deletes.ToImmutable()), _editLimits);
        return result;
    }

    private static Proto.ApplyEditsRequest ServiceLayerRequest(string serviceId, Proto.ServiceLayerEdits edit)
        => new()
        {
            ServiceId = serviceId,
            LayerId = edit.LayerId,
            Adds = { edit.Adds },
            Updates = { edit.Updates },
            Deletes = { edit.Deletes },
            ForceWrite = edit.ForceWrite,
            UseGlobalIds = edit.UseGlobalIds,
            DeleteGlobalIds = { edit.DeleteGlobalIds }
        };

    private readonly record struct ServiceEditCall(
        Proto.ApplyServiceEditsRequest Request, ServerCallContext Context, GrpcServiceEditsReceipts Receipts);

    private sealed record ServiceEditExecution(
        ServiceEditCall Call, PreparedGrpcEdit[] Edits, GrpcServiceEditsReceipts.Entry? Keyed)
    {
        internal required string Fingerprint { get; init; }
    }

    private static async Task<GrpcServiceEditsReceipts.Entry?> EnterServiceEditAsync(ServiceEditCall call)
    {
        var key = call.Request.IdempotencyKey.Trim();
        if (key.Length == 0)
        {
            return null;
        }
        var scope = GrpcServiceEditsReceipts.CreateScope(call.Context.GetHttpContext(),
            new GrpcServiceEditsReceipts.ScopeKey(call.Request.ServiceId, -1, key))
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
                Honua.Infrastructure.Security.IdempotencyCaller.UnbindableCallerMessage));
        return await call.Receipts.EnterAsync(scope, call.Context.CancellationToken).ConfigureAwait(false);
    }

    private static void EnsureServiceFingerprint(string? actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "This idempotency key belongs to a different service edit request."));
        }
    }

    private static Proto.ApplyServiceEditsResponse FilterServiceGlobalIds(
        PreparedGrpcEdit[] edits, Proto.ApplyServiceEditsResponse response)
    {
        foreach (var result in response.Results)
        {
            var edit = edits.SingleOrDefault(edit => edit.Call.Request.LayerId == result.LayerId);
            if (edit.Call.Request is null)
            {
                throw OutcomeMayHaveCommitted();
            }
            FilterResultGlobalIds(edit.GlobalIds, result.Results);
        }
        return response;
    }
}
