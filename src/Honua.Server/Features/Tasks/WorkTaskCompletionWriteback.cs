// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Assets;
using Honua.Server.Features.Assets;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionWriteback(
    WorkTaskAssetResolver assets,
    WorkTaskCompletionEdits edits,
    AssetCompletionCapabilities capabilities)
{
    public async Task<CompletionWritebackPreparation> PrepareAsync(
        HttpContext context, (WorkTask Task, WorkTaskCompletionRequest Request) completion, CancellationToken cancellationToken)
    {
        var (task, request) = completion;
        if (task.ProfileId is not Guid profileId || task.Status == WorkTaskStatus.Completed)
        {
            return new();
        }

        var asset = await assets.ResolveAsync(new WorkTaskAssetReference(profileId, task.ServiceId, task.AssetId!), context.User, cancellationToken).ConfigureAwait(false);
        if (asset.Error is { } error)
        {
            return new() { Error = error };
        }

        return await PrepareEditAsync(context, (asset, task, request), cancellationToken).ConfigureAwait(false);
    }

    private async Task<CompletionWritebackPreparation> PrepareEditAsync(
        HttpContext context, (WorkTaskAsset Asset, WorkTask Task, WorkTaskCompletionRequest Request) completion, CancellationToken cancellationToken)
    {
        var (asset, task, request) = completion;
        var target = asset.Target!.Value;
        var edit = BuildEdit(asset, task, request);
        var plan = new WorkTaskAssetWriteback(asset.Profile!.Version, target.StorageLayerId!.Value, null);
        if (edit.IsEmpty || !await capabilities.SupportsWritebackAsync(target, cancellationToken).ConfigureAwait(false))
        {
            return new() { Plan = plan };
        }

        if (!await assets.AuthorizeUpdateAsync(asset, context.User, cancellationToken).ConfigureAwait(false))
        {
            return CompletionWritebackPreparation.Fail("The mapped asset update is not permitted.");
        }

        return await edits.PrepareAsync(context, new CompletionMappedEdit(target, plan, edit)
        {
            ServiceId = task.ServiceId,
            CommandId = request.CommandId!.Value,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static UnifiedEditRequest BuildEdit(WorkTaskAsset asset, WorkTask task, WorkTaskCompletionRequest request)
    {
        var fields = asset.Profile!.Fields;
        var mapped = ImmutableDictionary.CreateBuilder<string, object?>();
        if (task.Type == WorkTaskType.Inspection)
        {
            if (fields.Condition is { } condition && request.Condition is int value)
            {
                mapped[condition] = value;
            }
            if (fields.LastInspected is { } inspected)
            {
                mapped[inspected] = request.ObservedAt!.Value.UtcDateTime;
            }
        }
        else if (task.Type != WorkTaskType.Request && fields.LastServiced is { } serviced)
        {
            mapped[serviced] = request.ObservedAt!.Value.UtcDateTime;
        }

        return mapped.Count == 0 ? default : Edit(asset.Feature!.Value, mapped.ToImmutable());
    }

    private static UnifiedEditRequest Edit(Feature feature, ImmutableDictionary<string, object?> mapped)
        => new()
        {
            Updates = [new EditFeature
            {
                ObjectId = feature.Id,
                Attributes = feature.Attributes.SetItems(mapped),
                UpdateMode = EditUpdateMode.Partial,
                PreserveOmittedGeometry = true,
                PreserveOmittedMaskedAttributes = true,
            }],
            Preconditions = [new FeatureEditPrecondition { ObjectId = feature.Id, ExpectedStateToken = FeatureStateToken.FromReadSnapshot(feature) }],
            TransactionOptions = EditTransactionOptions.WithRollback(),
        };

    public Task InvalidateAsync(CompletionWritebackPreparation preparation)
        => edits.InvalidateAsync(preparation);
}

internal sealed record CompletionWritebackPreparation
{
    public WorkTaskAssetWriteback? Plan { get; init; }
    public FeatureMutationOutboxScopeData? Scope { get; init; }
    public FormTargetMetadataResolution? Target { get; init; }
    public AssetFieldError? Error { get; init; }

    public static CompletionWritebackPreparation Fail(string message) => new() { Error = new("assetId", message) };
}
