// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Ogc.Common;

// Canonical pre-write and post-commit edit gate shared across OGC API Features and WFS-T:
// preserves active field-mask columns on updates/replaces, strips client editor-tracking
// stamps, evaluates attribute rules and owner-edit policy, stamps server tracking fields,
// checks contingent values, and runs plugin before/after hooks.
internal static class OgcProtocolEditGate
{
    internal readonly record struct Target(HttpContext Context, MetadataV2Object Resource)
    {
        public required int LayerId { get; init; }

        public required IFeatureReader Reader { get; init; }

        public string? ServiceName { get; init; }

        public int? PublicLayerId { get; init; }

        public IReadOnlyDictionary<long, Feature>? StoredRows { get; init; }

        public string InvalidDataFallbackMessage { get; init; } = "Invalid feature data.";

        public bool AlwaysLoadUpdateSnapshots { get; init; } = true;

        public bool FailWholeBatchOnRejection { get; init; } = true;
    }

    internal readonly record struct PreparedGate(
        FeatureEditBatch Batch,
        FeatureEditResult? Ended,
        IReadOnlyList<FeatureEditHookRejection> Rejections);

    private readonly record struct EffectiveEdits(
        FeatureEditBatch Batch,
        Dictionary<(FeatureEditOperationKind Kind, long Id), IReadOnlyDictionary<string, object?>> Rows);

    private readonly record struct EffectiveBatchCheck(
        EffectiveEdits Effective,
        IFeatureEditHooks Hooks);

    internal static async Task<PreparedGate> PrepareAsync(
        Target target,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        batch = await PreserveMaskedAttributesAsync(target, batch, cancellationToken).ConfigureAwait(false);
        batch = EditorTrackingBatch.OmitClientValues(target.Resource, batch);
        var repeatedUpdates = RepeatedUpdateRejections(batch);
        if (repeatedUpdates.Length > 0)
        {
            return new(batch, FeatureEditGate.RejectedResult(batch, repeatedUpdates), repeatedUpdates);
        }

        var hooks = ResolveHooks(target.Context);
        var needsEffectiveSnapshots = target.AlwaysLoadUpdateSnapshots
            || target.Resource.AttributeRules.Count > 0
            || target.Resource.ContingentValueGroups.Count > 0
            || hooks.HasHooks;

        EffectiveEdits effective;
        if (needsEffectiveSnapshots)
        {
            var loaded = await ReadEffectiveUpdatesAsync(target, batch, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                return new(batch, OwnerEditPolicyFieldAccess.DeniedEditResult(batch, "Edit not permitted."), []);
            }

            effective = loaded.Value;
        }
        else
        {
            effective = new(batch, []);
        }

        return await CheckEffectiveBatchAsync(target, new EffectiveBatchCheck(effective, hooks), cancellationToken).ConfigureAwait(false);
    }

    internal static Task RunAfterAsync(Target target, FeatureEditBatch batch, FeatureEditResult result)
    {
        var hooks = ResolveHooks(target.Context);
        return hooks.HasHooks
            ? hooks.RunAfterAsync(Scope(target), new FeatureEditOutcome(batch, result), CancellationToken.None)
            : Task.CompletedTask;
    }

    private static async Task<FeatureEditBatch> PreserveMaskedAttributesAsync(
        Target target,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        if (target.Context.RequestServices?.GetService<IFieldMaskSource>() is not { } maskSource)
        {
            return batch;
        }

        var maskedFields = await maskSource.ResolveAsync(target.Resource, cancellationToken).ConfigureAwait(false);
        if (maskedFields.IsDefaultOrEmpty)
        {
            return batch;
        }

        var maskedSet = maskedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return batch with
        {
            Updates = batch.Updates.IsDefaultOrEmpty
                ? batch.Updates
                : [.. batch.Updates.Select(feature => MergeMaskedAttributes(feature, maskedFields, maskedSet))],
            Operations = batch.Operations.IsDefaultOrEmpty
                ? batch.Operations
                : [.. batch.Operations.Select(operation =>
                    operation.Kind == FeatureEditOperationKind.Update && operation.Feature is { } feature
                        ? operation with { Feature = MergeMaskedAttributes(feature, maskedFields, maskedSet) }
                        : operation)],
        };
    }

    private static Feature MergeMaskedAttributes(
        Feature feature,
        ImmutableArray<string> maskedFields,
        HashSet<string> maskedSet)
    {
        var preserved = feature.PreservedAttributeNames.IsDefaultOrEmpty
            ? maskedFields.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray()
            : feature.PreservedAttributeNames
                .Concat(maskedFields)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        var removals = feature.ExplicitAttributeRemovals.IsDefaultOrEmpty
            ? feature.ExplicitAttributeRemovals
            : [.. feature.ExplicitAttributeRemovals.Where(name => !maskedSet.Contains(name))];
        return feature with
        {
            PreservedAttributeNames = preserved,
            ExplicitAttributeRemovals = removals,
        };
    }

    private static async Task<PreparedGate> CheckEffectiveBatchAsync(
        Target target,
        EffectiveBatchCheck batchCheck,
        CancellationToken cancellationToken)
    {
        var check = CreateFeatureEditCheck(target, batchCheck.Effective);
        var rules = FeatureEditGate.ApplyAttributeRules(check);
        var principal = ServiceDataEditorAuthorization.ResolveEditPrincipal(target.Context);
        var prepared = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(
            target.Reader,
            target.LayerId,
            target.Resource.OwnerEditPolicy,
            rules.Batch,
            principal,
            cancellationToken).ConfigureAwait(false);
        if (!prepared.IsAllowed)
        {
            return new(rules.Batch, OwnerEditPolicyFieldAccess.DeniedEditResult(rules.Batch, prepared.DenialReason ?? "Edit not permitted."), []);
        }

        var now = (target.Context.RequestServices?.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var checkedBatch = EditorTrackingBatch.Stamp(
            target.Resource,
            prepared.Batch,
            new EditorTrackingStamp(principal.Name, field => TrackingTimeValue(field, now)));
        var rejections = await FeatureEditGate.CheckAsync(
            batchCheck.Hooks,
            check with { Batch = checkedBatch, Rejections = rules.Rejections },
            cancellationToken).ConfigureAwait(false);
        return FinalizeCheckRejections(target, checkedBatch, rejections);
    }

    private static FeatureEditCheck CreateFeatureEditCheck(Target target, EffectiveEdits effective)
        => new(target.Resource, Scope(target), effective.Batch)
        {
            EffectiveAttributes = row => EffectiveAttributes(row, effective.Rows),
            UnsupportedExpressions = target.Context.RequestServices?.GetService<ILoggerFactory>() is { } logging
                ? new AttributeRuleWarningSink(logging.CreateLogger("Honua.Features.AttributeRules"))
                : null,
        };

    private static PreparedGate FinalizeCheckRejections(
        Target target,
        FeatureEditBatch checkedBatch,
        IReadOnlyList<FeatureEditHookRejection> rejections)
    {
        if (rejections.Count == 0)
        {
            return new(checkedBatch, null, []);
        }

        var safeRejections = Safe(rejections, target.InvalidDataFallbackMessage);
        if (target.FailWholeBatchOnRejection || checkedBatch.RollbackOnFailure)
        {
            return new(checkedBatch, FeatureEditGate.RejectedResult(checkedBatch, safeRejections), safeRejections);
        }

        var remaining = FeatureEditGate.WithoutRejected(checkedBatch, safeRejections);
        if (remaining.IsEmpty)
        {
            var allRejected = FeatureEditGate.WithRejectedRows(
                FeatureEditResult.Success(0, 0, 0),
                checkedBatch,
                safeRejections);
            return new(checkedBatch, allRejected, safeRejections);
        }

        return new(checkedBatch, null, safeRejections);
    }

    private static async Task<EffectiveEdits?> ReadEffectiveUpdatesAsync(
        Target target,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        var rows = FeatureEditRows.Enumerate(batch);
        var updates = rows
            .Where(static row => row.Kind == FeatureEditOperationKind.Update && row.Feature is { } feature &&
                (feature.PreserveOmittedMaskedAttributes || !feature.PreservedAttributeNames.IsDefaultOrEmpty))
            .Select(static row => row.Feature!.Value)
            .ToArray();
        var effective = new Dictionary<(FeatureEditOperationKind Kind, long Id), IReadOnlyDictionary<string, object?>>();
        var deletes = target.Resource.AttributeRules.Count == 0 ? [] : rows
            .Where(static row => row.Kind == FeatureEditOperationKind.Delete)
            .Select(static row => row.ObjectId!.Value)
            .ToArray();
        var ids = updates.Select(static row => row.Id).Concat(deletes).Distinct().ToImmutableArray();
        if (ids.IsEmpty)
        {
            return new(batch, effective);
        }

        var page = await ReadSnapshotsAsync(target, (batch.VersionContext, ids), cancellationToken).ConfigureAwait(false);
        var stored = page.Items.IsDefaultOrEmpty ? new Dictionary<long, Feature>() : page.Items.ToDictionary(static row => row.Id);
        return BindEffectiveSnapshots(batch, updates, (deletes, stored));
    }

    private static EffectiveEdits? BindEffectiveSnapshots(
        FeatureEditBatch batch,
        Feature[] updates,
        (long[] Deletes, Dictionary<long, Feature> Stored) snapshots)
    {
        var effective = new Dictionary<(FeatureEditOperationKind Kind, long Id), IReadOnlyDictionary<string, object?>>();
        var preconditions = batch.Preconditions.IsDefault ? [] : batch.Preconditions.ToList();
        foreach (var update in updates)
        {
            if (!snapshots.Stored.TryGetValue(update.Id, out var snapshot))
            {
                return null;
            }

            effective[(FeatureEditOperationKind.Update, update.Id)] = MergeEffectiveAttributes(update, snapshot);
            BindSnapshot(preconditions, update, snapshot);
        }

        foreach (var id in snapshots.Deletes)
        {
            if (!snapshots.Stored.TryGetValue(id, out var snapshot))
            {
                return null;
            }

            effective[(FeatureEditOperationKind.Delete, id)] = snapshot.Attributes;
            BindSnapshot(preconditions, Feature.Create(id, null), snapshot);
        }

        return new(batch with { Preconditions = [.. preconditions] }, effective);
    }

    private static Task<QueryResult<Feature>> ReadSnapshotsAsync(
        Target target,
        (VersionContext? Version, ImmutableArray<long> Ids) query,
        CancellationToken cancellationToken)
        => target.Reader.QueryAsync(target.LayerId, new FeatureQuery
        {
            ObjectIds = query.Ids,
            Limit = query.Ids.Length,
            // Restore hidden attributes for validation only; retain the enforced row filter.
            EnforcedMaskedFields = ImmutableArray<string>.Empty,
            VersionContext = query.Version,
        }, cancellationToken);

    private static void BindSnapshot(List<FeatureEditPrecondition> preconditions, Feature update, Feature snapshot)
    {
        var omitted = snapshot.Attributes.Keys.Except(update.Attributes.Keys, StringComparer.OrdinalIgnoreCase);
        var existing = preconditions.FindIndex(condition => condition.ObjectId == update.Id);
        if (existing >= 0)
        {
            var condition = preconditions[existing];
            preconditions[existing] = condition with
            {
                MaskedFields = [.. (condition.MaskedFields.IsDefault ? [] : condition.MaskedFields).Union(omitted, StringComparer.OrdinalIgnoreCase)],
            };
            return;
        }

        preconditions.Add(new FeatureEditPrecondition
        {
            ObjectId = update.Id,
            ExpectedStateToken = FeatureStateToken.FromReadSnapshot(snapshot),
            // Omitted fields need not be disclosed in a concurrency conflict response.
            MaskedFields = [.. omitted],
        });
    }

    private static Dictionary<string, object?> MergeEffectiveAttributes(Feature update, Feature snapshot)
    {
        var attributes = new Dictionary<string, object?>(update.Attributes, StringComparer.OrdinalIgnoreCase);
        var preserved = update.PreservedAttributeNames.IsDefaultOrEmpty
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : update.PreservedAttributeNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = update.ExplicitAttributeRemovals.IsDefaultOrEmpty
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : update.ExplicitAttributeRemovals.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in snapshot.Attributes)
        {
            if (!name.Equals("objectid", StringComparison.OrdinalIgnoreCase) && !attributes.ContainsKey(name) &&
                (preserved.Contains(name) || (update.PreserveOmittedMaskedAttributes && !removed.Contains(name))))
            {
                attributes[name] = value;
            }
        }

        return attributes;
    }

    private static object? TrackingTimeValue(MetadataV2Field? field, DateTimeOffset now) => field?.Type switch
    {
        null or MetadataV2FieldType.DateTime => now.UtcDateTime,
        MetadataV2FieldType.Date => DateOnly.FromDateTime(now.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        MetadataV2FieldType.String or MetadataV2FieldType.TimestampOffset => now.ToString("O", CultureInfo.InvariantCulture),
        MetadataV2FieldType.BigInteger or MetadataV2FieldType.Double => now.ToUnixTimeMilliseconds(),
        _ => null,
    };

    private static IReadOnlyDictionary<string, object?> EffectiveAttributes(
        FeatureEditRow row,
        Dictionary<(FeatureEditOperationKind Kind, long Id), IReadOnlyDictionary<string, object?>> stored)
    {
        var attributes = row.Feature?.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        if (row.Kind == FeatureEditOperationKind.Create || !stored.TryGetValue((row.Kind, row.ObjectId ?? 0), out var snapshot))
        {
            return attributes;
        }

        var merged = new Dictionary<string, object?>(snapshot, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in attributes)
        {
            merged[name] = value;
        }

        return merged;
    }

    private static FeatureEditHookRejection[] RepeatedUpdateRejections(FeatureEditBatch batch)
        => [.. FeatureEditRows.Enumerate(batch)
            .Where(static row => row.Kind == FeatureEditOperationKind.Update)
            .GroupBy(static row => row.ObjectId)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group.Select(static row => new FeatureEditHookRejection(
                row.Kind, row.Index, "An edit batch cannot update the same feature more than once.")))];

    private static IFeatureEditHooks ResolveHooks(HttpContext context)
        => context.RequestServices?.GetService<IFeatureEditHooks>() ?? NoOpFeatureEditHooks.Instance;

    private static FeatureEditHookScope Scope(Target target)
        => new(target.ServiceName ?? target.Resource.Metadata.Id, target.PublicLayerId ?? target.LayerId)
        {
            LayerName = target.Resource.Metadata.Name,
            UserName = target.Context.User?.Identity?.Name,
            TraceId = target.Context.TraceIdentifier,
            StoredRows = target.StoredRows,
        };

    private static IReadOnlyList<FeatureEditHookRejection> Safe(
        IReadOnlyList<FeatureEditHookRejection> rejections,
        string fallbackMessage)
        => [.. rejections.Select(rejection => rejection with
        {
            Message = ErrorMessageSanitizer.Sanitize(rejection.Message, fallbackMessage),
        })];
}
