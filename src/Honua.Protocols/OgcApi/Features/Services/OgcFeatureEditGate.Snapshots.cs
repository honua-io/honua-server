// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Query;

namespace Honua.Protocols.Ogc.Api.Features.Services;

internal static partial class OgcFeatureEditGate
{
    private static async Task<(FeatureEditBatch Batch, Dictionary<long, IReadOnlyDictionary<string, object?>> Rows)?> ReadEffectiveUpdatesAsync(
        Target target,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        var updates = FeatureEditRows.Enumerate(batch)
            .Where(static row => row.Kind == FeatureEditOperationKind.Update && row.Feature is { } feature &&
                (feature.PreserveOmittedMaskedAttributes || !feature.PreservedAttributeNames.IsDefaultOrEmpty))
            .Select(static row => row.Feature!.Value)
            .ToArray();
        var effective = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
        if (updates.Length == 0)
        {
            return (batch, effective);
        }

        var ids = updates.Select(static row => row.Id).Distinct().ToImmutableArray();
        var page = await ReadSnapshotsAsync(target, batch, ids, cancellationToken).ConfigureAwait(false);
        var stored = page.Items.IsDefaultOrEmpty ? new Dictionary<long, Feature>() : page.Items.ToDictionary(static row => row.Id);
        var preconditions = batch.Preconditions.IsDefault ? [] : batch.Preconditions.ToList();
        foreach (var update in updates)
        {
            if (!stored.TryGetValue(update.Id, out var snapshot))
            {
                return null;
            }

            effective[update.Id] = MergeEffectiveAttributes(update, snapshot);
            BindSnapshot(preconditions, update, snapshot);
        }

        return (batch with { Preconditions = [.. preconditions] }, effective);
    }

    private static Task<QueryResult<Feature>> ReadSnapshotsAsync(
        Target target,
        FeatureEditBatch batch,
        ImmutableArray<long> ids,
        CancellationToken cancellationToken)
        => target.Reader.QueryAsync(target.LayerId, new FeatureQuery
        {
            ObjectIds = ids,
            OutFields = ["*"],
            Limit = ids.Length,
            // Restore hidden attributes for validation only; retain the enforced row filter.
            EnforcedMaskedFields = ImmutableArray<string>.Empty,
            VersionContext = batch.VersionContext,
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

    private static IReadOnlyDictionary<string, object?> MergeEffectiveAttributes(Feature update, Feature snapshot)
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
