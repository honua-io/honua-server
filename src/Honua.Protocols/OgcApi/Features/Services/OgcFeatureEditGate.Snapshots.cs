// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Query;

namespace Honua.Protocols.Ogc.Api.Features.Services;

internal static partial class OgcFeatureEditGate
{
    private readonly record struct EffectiveEdits(
        FeatureEditBatch Batch,
        Dictionary<(FeatureEditOperationKind Kind, long Id), IReadOnlyDictionary<string, object?>> Rows);

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
            .Select(static row => row.ObjectId!.Value).ToArray();
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

}
