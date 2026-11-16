// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Grpc.Core;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Grpc;

// gRPC edits pass the same contingent-value and plugin checks as GeoServices applyEdits. Under
// rollback_on_failure a rejected row fails the call like any invalid attribute; otherwise only the
// rejected rows fail and the rest are written.
internal sealed partial class HonuaFeatureService
{
    private sealed record EditGate(
        FeatureEditBatch Batch,
        IReadOnlyList<FeatureEditHookRejection> Rejections,
        IFeatureEditHooks Hooks,
        FeatureEditHookScope Scope)
    {
        public FeatureEditBatch Written { get; } = FeatureEditGate.WithoutRejected(Batch, Rejections);

        public FeatureEditBatch Without(FeatureEditBatch batch) => FeatureEditGate.WithoutRejected(batch, Rejections);

        public FeatureEditResult Merge(FeatureEditResult written) => FeatureEditGate.WithRejectedRows(written, Batch, Rejections);

        public Task RunAfterAsync(FeatureEditResult written)
            => Hooks.HasHooks ? Hooks.RunAfterAsync(Scope, Written, written, CancellationToken.None) : Task.CompletedTask;
    }

    private static async Task<EditGate> CheckEditGateAsync(
        EditCall call,
        FeatureEditBatch batch,
        IReadOnlyDictionary<long, Feature> storedRows)
    {
        var httpContext = call.Context.GetHttpContext();
        var hooks = httpContext.RequestServices?.GetService<IFeatureEditHooks>() ?? NoOpFeatureEditHooks.Instance;
        var scope = new FeatureEditHookScope(
            call.Request.ServiceId ?? "unknown",
            call.Request.LayerId,
            call.Layer.Resource.Metadata.Name,
            httpContext.User?.Identity?.Name,
            httpContext.TraceIdentifier);
        var check = new FeatureEditCheck(call.Layer.Resource, batch, row => EffectiveAttributes(row, storedRows));
        var rejections = await FeatureEditGate.CheckAsync(hooks, scope, check, call.Context.CancellationToken)
            .ConfigureAwait(false);
        if (rejections.Count > 0 && batch.RollbackOnFailure)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                BoundEditValidationMessage(rejections[0].Message, "Invalid feature data.")));
        }

        return new EditGate(batch, rejections, hooks, scope);
    }

    // A partial update is checked as the stored row with the update's values applied.
    private static IReadOnlyDictionary<string, object?> EffectiveAttributes(
        FeatureEditRow row,
        IReadOnlyDictionary<long, Feature> storedRows)
    {
        var attributes = row.Feature?.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        if (row.Kind != FeatureEditOperationKind.Update || !storedRows.TryGetValue(row.ObjectId ?? 0, out var stored))
        {
            return attributes;
        }

        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in stored.Attributes.Concat(attributes))
        {
            merged[name] = value;
        }

        return merged;
    }
}
