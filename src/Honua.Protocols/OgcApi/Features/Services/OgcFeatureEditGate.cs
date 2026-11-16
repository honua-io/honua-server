// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Ogc.Api.Features.Services;

// OGC API Features edits pass the owner policy, then the same contingent-value and plugin checks as
// GeoServices applyEdits. Every OGC edit row already carries its full effective attributes: PATCH
// merges the stored row before the write, and create and replace send the whole row.
internal static class OgcFeatureEditGate
{
    internal readonly record struct Target(HttpContext Context, int LayerId, MetadataV2Object Resource, string? ServiceName);

    // Ended is the result that stops the edit before the write; any rejection fails the whole request.
    internal static async Task<(FeatureEditBatch Batch, FeatureEditResult? Ended)> PrepareAsync(
        Target target,
        IFeatureReader reader,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        var prepared = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(
            reader,
            target.LayerId,
            target.Resource.OwnerEditPolicy,
            batch,
            ServiceDataEditorAuthorization.ResolveEditPrincipal(target.Context),
            cancellationToken).ConfigureAwait(false);
        if (!prepared.IsAllowed)
        {
            return (batch, OwnerEditPolicyFieldAccess.DeniedEditResult(batch, prepared.DenialReason ?? "Edit not permitted."));
        }

        var rejections = await FeatureEditGate.CheckAsync(
            ResolveHooks(target.Context),
            Scope(target),
            new FeatureEditCheck(target.Resource, prepared.Batch),
            cancellationToken).ConfigureAwait(false);
        return (prepared.Batch, rejections.Count == 0 ? null : FeatureEditGate.RejectedResult(prepared.Batch, rejections));
    }

    internal static Task RunAfterAsync(Target target, FeatureEditBatch batch, FeatureEditResult result)
    {
        var hooks = ResolveHooks(target.Context);
        return hooks.HasHooks
            ? hooks.RunAfterAsync(Scope(target), batch, result, CancellationToken.None)
            : Task.CompletedTask;
    }

    private static IFeatureEditHooks ResolveHooks(HttpContext context)
        => context.RequestServices?.GetService<IFeatureEditHooks>() ?? NoOpFeatureEditHooks.Instance;

    private static FeatureEditHookScope Scope(Target target)
        => new(
            target.ServiceName ?? target.Resource.Metadata.Id,
            target.LayerId,
            target.Resource.Metadata.Name,
            target.Context.User?.Identity?.Name,
            target.Context.TraceIdentifier);
}
