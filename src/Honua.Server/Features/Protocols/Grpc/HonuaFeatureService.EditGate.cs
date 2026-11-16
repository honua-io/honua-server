// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Grpc.Core;
using Honua.Core.Features.Edit;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Grpc;

// gRPC edits pass the same contingent-value and plugin checks as GeoServices applyEdits. Under
// rollback_on_failure a rejected row fails the call like any invalid attribute; otherwise only the
// rejected rows fail and the rest are written.
internal sealed partial class HonuaFeatureService
{
    private sealed record EditGate(FeatureEditBatch Batch, IReadOnlyList<FeatureEditHookRejection> Rejections)
    {
        public required IFeatureEditHooks Hooks { get; init; }

        public required FeatureEditHookScope Scope { get; init; }

        public FeatureEditBatch Written { get; } = FeatureEditGate.WithoutRejected(Batch, Rejections);

        public FeatureEditBatch Without(FeatureEditBatch batch) => FeatureEditGate.WithoutRejected(batch, Rejections);

        public FeatureEditResult Merge(FeatureEditResult written) => FeatureEditGate.WithRejectedRows(written, Batch, Rejections);

        public Task RunAfterAsync(FeatureEditResult written)
            => Hooks.HasHooks
                ? Hooks.RunAfterAsync(Scope, new FeatureEditOutcome(Written, written), CancellationToken.None)
                : Task.CompletedTask;
    }

    private static async Task<EditGate> CheckEditGateAsync(
        EditCall call,
        FeatureEditPreparation prepared,
        IReadOnlyDictionary<long, Feature> storedRows)
    {
        var batch = prepared.Batch;
        ValidateUniqueUpdateTargets(batch);
        storedRows = ValidationSnapshots(storedRows);

        var httpContext = call.Context.GetHttpContext();
        var hooks = httpContext.RequestServices?.GetService<IFeatureEditHooks>() ?? NoOpFeatureEditHooks.Instance;
        var scope = new FeatureEditHookScope(call.Request.ServiceId ?? "unknown", call.Request.LayerId)
        {
            LayerName = call.Layer.Resource.Metadata.Name,
            UserName = httpContext.User?.Identity?.Name,
            TraceId = httpContext.TraceIdentifier,
            StoredRows = storedRows,
        };
        var check = new FeatureEditCheck(call.Layer.Resource, scope, batch)
        {
            EffectiveAttributes = row => EffectiveAttributes(row, storedRows),
            Rejections = prepared.Rejections,
        };
        var rejections = await FeatureEditGate.CheckAsync(hooks, check, call.Context.CancellationToken)
            .ConfigureAwait(false);
        if (rejections.Count > 0 && batch.RollbackOnFailure)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                ErrorMessageSanitizer.Sanitize(rejections[0].Message, "Invalid feature data.")));
        }

        IReadOnlyList<FeatureEditHookRejection> bounded =
        [
            .. rejections.Select(static rejection => rejection with
            {
                Message = ErrorMessageSanitizer.Sanitize(rejection.Message, "Invalid feature data."),
            }),
        ];
        return new EditGate(batch, bounded) { Hooks = hooks, Scope = scope };
    }

    private FeatureEditPreparation ApplyEditRules(EditCall call, FeatureEditBatch batch, IReadOnlyDictionary<long, Feature> storedRows)
    {
        var snapshots = ValidationSnapshots(storedRows);
        return FeatureEditGate.ApplyAttributeRules(new FeatureEditCheck(
            call.Layer.Resource, new FeatureEditHookScope(call.Request.ServiceId ?? "unknown", call.Request.LayerId), batch)
        {
            EffectiveAttributes = row => EffectiveAttributes(row, snapshots),
            UnsupportedExpressions = new AttributeRuleWarningSink(_logger),
        });
    }

    // Keep request geometry for change-event semantics, and include calculated attributes in events.
    private static FeatureEditBatch CalculatedEventBatch(FeatureEditBatch request, FeatureEditBatch prepared)
        => request with
        {
            Creates = [.. request.Creates.Select((feature, index) => feature with { Attributes = prepared.Creates[index].Attributes })],
            Updates = [.. request.Updates.Select((feature, index) => feature with { Attributes = prepared.Updates[index].Attributes })],
        };

    private static Dictionary<long, Feature> ValidationSnapshots(IReadOnlyDictionary<long, Feature> storedRows)
        => storedRows.ToDictionary(static pair => pair.Key, static pair => pair.Value with
        {
            Attributes = pair.Value.EditValidationAttributes ?? pair.Value.Attributes
        });

    private static void ValidateUniqueUpdateTargets(FeatureEditBatch batch)
    {
        // Every update is validated against its stored snapshot. Reject repeated targets rather
        // than letting independently valid partial updates combine into an invalid stored row.
        if (batch.Updates.Select(static feature => feature.Id).Distinct().Count() != batch.Updates.Length)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "An edit batch cannot update the same feature more than once."));
        }

    }

    // A partial update is checked as the stored row with the update's values applied.
    private static IReadOnlyDictionary<string, object?> EffectiveAttributes(
        FeatureEditRow row,
        IReadOnlyDictionary<long, Feature> storedRows)
    {
        var attributes = row.Feature?.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        if (row.Kind == FeatureEditOperationKind.Create || !storedRows.TryGetValue(row.ObjectId ?? 0, out var stored))
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
