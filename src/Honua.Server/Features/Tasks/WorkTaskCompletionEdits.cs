// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
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

        // The mapped columns get the same type, editability and domain checks as a client edit.
        var fields = FeatureAttributeValidator.Validate(target.Resource!, completion.Mapped, new AttributeValidationOptions(
            AttributeValidationProfile.GeoServices, AttributeMutationOperation.Update, ExactValueCoercion.CanonicalText));
        if (!fields.IsValid)
        {
            return Refused(completion, fields.ErrorMessage ?? "The mapped asset update cannot be applied safely.");
        }

        var batch = EditorTrackingBatch.OmitClientValues(target.Resource!,
            edits.ToFeatureEditBatch(edits.OptimizeEdit(edit, target.Resource!), target.Resource!));
        var (checkedBatch, denied, rejected) = await CheckAsync(context, (completion, batch), cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return CompletionWritebackPreparation.Fail(denied);
        }

        if (rejected is not null)
        {
            return Refused(completion, rejected);
        }

        var scope = await events.ResolveOutboxScopeAsync(context, target.StorageLayerId!.Value, "tasks",
            serviceId: completion.ServiceId, requestId: completion.CommandId.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new() { Plan = plan with { Batch = checkedBatch }, Scope = scope, Target = target };
    }

    // The same pre-write path as protocol edits: attribute rules, owner policy, editor
    // tracking stamped with the completing principal, then contingent values and plugin hooks.
    private async Task<(FeatureEditBatch Batch, string? Denied, string? Rejected)> CheckAsync(
        HttpContext context, (CompletionMappedEdit Completion, FeatureEditBatch Batch) edit, CancellationToken cancellationToken)
    {
        var (completion, batch) = edit;
        var target = completion.Target;
        var resource = target.Resource!;
        var principal = ServiceDataEditorAuthorization.ResolveEditPrincipal(context);
        var check = new FeatureEditCheck(resource, Scope(context, completion), batch)
        {
            // The write-back row already carries the stored attributes it updates.
            EffectiveAttributes = static row => row.Feature?.Attributes ?? ImmutableDictionary<string, object?>.Empty,
        };
        var rules = FeatureEditGate.ApplyAttributeRules(check);
        var prepared = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(features,
            target.StorageLayerId!.Value, resource.OwnerEditPolicy, rules.Batch, principal, cancellationToken).ConfigureAwait(false);
        if (!prepared.IsAllowed)
        {
            return (rules.Batch, "The mapped asset update is not permitted.", null);
        }

        var now = (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var stamped = EditorTrackingBatch.Stamp(resource, prepared.Batch,
            new EditorTrackingStamp(principal.Name, field => TrackingTimeValue(field, now)));
        var hooks = context.RequestServices.GetService<IFeatureEditHooks>() ?? NoOpFeatureEditHooks.Instance;
        var rejections = await FeatureEditGate.CheckAsync(hooks, check with { Batch = stamped, Rejections = rules.Rejections },
            cancellationToken).ConfigureAwait(false);
        return (stamped, null, rejections.Count == 0 ? null : "The mapped asset update was rejected by the layer's edit rules.");
    }

    // Field and edit-rule refusals depend on whether the write happens, which the
    // completion transaction decides from the summary watermark.
    private static CompletionWritebackPreparation Refused(CompletionMappedEdit completion, string message)
        => new() { Plan = completion.Plan with { Batch = null, Refusal = message }, Target = completion.Target };

    private static FeatureEditHookScope Scope(HttpContext context, CompletionMappedEdit completion)
        => new(completion.ServiceId, completion.Target.StorageLayerId!.Value)
        {
            LayerName = completion.Target.Resource!.Metadata.Name,
            UserName = context.User?.Identity?.Name,
            TraceId = context.TraceIdentifier,
        };

    private static object? TrackingTimeValue(MetadataV2Field? field, DateTimeOffset now) => field?.Type switch
    {
        null or MetadataV2FieldType.DateTime => now.UtcDateTime,
        MetadataV2FieldType.Date => DateOnly.FromDateTime(now.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        MetadataV2FieldType.String or MetadataV2FieldType.TimestampOffset => now.ToString("O", CultureInfo.InvariantCulture),
        MetadataV2FieldType.BigInteger or MetadataV2FieldType.Double => now.ToUnixTimeMilliseconds(),
        _ => null,
    };

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

    public required ImmutableDictionary<string, object?> Mapped { get; init; }
}
