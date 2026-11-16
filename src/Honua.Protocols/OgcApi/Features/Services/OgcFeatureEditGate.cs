// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Ogc.Api.Features.Services;

// OGC API Features edits pass the owner policy, then the same contingent-value and plugin checks as
// GeoServices applyEdits. PATCH validation restores masked values internally without adding them
// to the submitted batch or returning them to the caller.
internal static partial class OgcFeatureEditGate
{
    // LayerId is the storage layer the write goes to; hooks are told the published layer in Request.
    internal readonly record struct Target(HttpContext Context, MetadataV2Object Resource, OgcFeaturesEditRequest Request)
    {
        public required int LayerId { get; init; }

        public required IFeatureReader Reader { get; init; }
    }

    // Ended is the result that stops the edit before the write; any rejection fails the whole request.
    internal static async Task<(FeatureEditBatch Batch, FeatureEditResult? Ended)> PrepareAsync(
        Target target,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        var repeatedUpdates = RepeatedUpdateRejections(batch);
        if (repeatedUpdates.Length > 0)
        {
            return (batch, FeatureEditGate.RejectedResult(batch, repeatedUpdates));
        }

        var prepared = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(
            target.Reader,
            target.LayerId,
            target.Resource.OwnerEditPolicy,
            batch,
            ServiceDataEditorAuthorization.ResolveEditPrincipal(target.Context),
            cancellationToken).ConfigureAwait(false);
        if (!prepared.IsAllowed)
        {
            return (batch, OwnerEditPolicyFieldAccess.DeniedEditResult(batch, prepared.DenialReason ?? "Edit not permitted."));
        }

        var effective = await ReadEffectiveUpdatesAsync(target, prepared.Batch, cancellationToken).ConfigureAwait(false);
        if (effective is null)
        {
            return (prepared.Batch, OwnerEditPolicyFieldAccess.DeniedEditResult(prepared.Batch, "Edit not permitted."));
        }

        return await CheckEffectiveBatchAsync(target, effective.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(FeatureEditBatch Batch, FeatureEditResult? Ended)> CheckEffectiveBatchAsync(
        Target target,
        (FeatureEditBatch Batch, Dictionary<long, IReadOnlyDictionary<string, object?>> Rows) effective,
        CancellationToken cancellationToken)
    {
        var checkedBatch = effective.Batch;
        var rejections = await FeatureEditGate.CheckAsync(
            ResolveHooks(target.Context),
            new FeatureEditCheck(target.Resource, Scope(target), checkedBatch)
            {
                EffectiveAttributes = row => row.Kind == FeatureEditOperationKind.Update && effective.Rows.TryGetValue(row.ObjectId ?? 0, out var attributes)
                    ? attributes
                    : row.Feature?.Attributes ?? ImmutableDictionary<string, object?>.Empty,
            },
            cancellationToken).ConfigureAwait(false);
        return (checkedBatch, rejections.Count == 0 ? null : FeatureEditGate.RejectedResult(checkedBatch, Safe(rejections)));
    }

    private static FeatureEditHookRejection[] RepeatedUpdateRejections(FeatureEditBatch batch)
        => [.. FeatureEditRows.Enumerate(batch)
            .Where(static row => row.Kind == FeatureEditOperationKind.Update)
            .GroupBy(static row => row.ObjectId)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group.Select(static row => new FeatureEditHookRejection(
                row.Kind, row.Index, "An edit batch cannot update the same feature more than once.")))];

    internal static Task RunAfterAsync(Target target, FeatureEditBatch batch, FeatureEditResult result)
    {
        var hooks = ResolveHooks(target.Context);
        return hooks.HasHooks
            ? hooks.RunAfterAsync(Scope(target), new FeatureEditOutcome(batch, result), CancellationToken.None)
            : Task.CompletedTask;
    }

    private static IFeatureEditHooks ResolveHooks(HttpContext context)
        => context.RequestServices?.GetService<IFeatureEditHooks>() ?? NoOpFeatureEditHooks.Instance;

    private static FeatureEditHookScope Scope(Target target)
        => new(target.Request.ServiceName ?? target.Resource.Metadata.Id, target.Request.PublicLayerId ?? target.LayerId)
        {
            LayerName = target.Resource.Metadata.Name,
            UserName = target.Context.User?.Identity?.Name,
            TraceId = target.Context.TraceIdentifier,
            StoredRows = StoredRows(target.Request),
        };

    private static Dictionary<long, Feature>? StoredRows(OgcFeaturesEditRequest request)
    {
        var stored = new Dictionary<long, Feature>();
        if (request is { StoredFeature: { } single, ObjectId: { } objectId })
        {
            stored[objectId] = single;
        }

        foreach (var operation in request.BatchOperations.IsDefault ? [] : request.BatchOperations)
        {
            if (operation is { StoredFeature: { } row, ObjectId: { } id })
            {
                stored[id] = row;
            }
        }

        return stored.Count == 0 ? null : stored;
    }

    private static IReadOnlyList<FeatureEditHookRejection> Safe(IReadOnlyList<FeatureEditHookRejection> rejections)
        => [.. rejections.Select(static rejection => rejection with
        {
            Message = ErrorMessageSanitizer.Sanitize(rejection.Message, OgcFeatureEditFailureMapper.InvalidDataMessage),
        })];
}
