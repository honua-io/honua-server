// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionEdits(
    IEditProcessor edits,
    IFeatureReader features,
    FeatureMutationEventService events)
{
    public async Task<CompletionWritebackPreparation> PrepareAsync(
        HttpContext context, CompletionMappedEdit completion, CancellationToken cancellationToken)
    {
        var (target, plan, edit) = completion;
        if (!events.OutboxEnabled)
        {
            return CompletionWritebackPreparation.Fail("Transactional feature events are unavailable.");
        }

        var validation = edits.ValidateEdit(edit, target.Resource!);
        if (!validation.IsValid)
        {
            return CompletionWritebackPreparation.Fail(validation.ErrorMessage ?? "The mapped asset update cannot be applied safely.");
        }

        var batch = edits.ToFeatureEditBatch(edits.OptimizeEdit(edit, target.Resource!), target.Resource!);
        var prepared = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(features, target.StorageLayerId!.Value,
            target.Resource!.OwnerEditPolicy, batch, ServiceDataEditorAuthorization.ResolveEditPrincipal(context), cancellationToken).ConfigureAwait(false);
        if (!prepared.IsAllowed)
        {
            return CompletionWritebackPreparation.Fail("The mapped asset update is not permitted.");
        }

        var scope = await events.ResolveOutboxScopeAsync(context, target.StorageLayerId!.Value, "tasks",
            serviceId: completion.ServiceId, requestId: completion.CommandId.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new() { Plan = plan with { Batch = prepared.Batch }, Scope = scope, Target = target };
    }

    public Task InvalidateAsync(CompletionWritebackPreparation preparation)
        => preparation.Target is { Publication: { } publication, StorageLayerId: int layerId }
            ? events.InvalidatePublishedLayerAsync(publication, layerId)
            : Task.CompletedTask;
}

internal sealed record CompletionMappedEdit(
    FormTargetMetadataResolution Target, WorkTaskAssetWriteback Plan, UnifiedEditRequest Edit)
{
    public required string ServiceId { get; init; }
    public required Guid CommandId { get; init; }
}
