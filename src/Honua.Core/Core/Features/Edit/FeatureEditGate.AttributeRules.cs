// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Edit;

public sealed record FeatureEditPreparation(FeatureEditBatch Batch, IReadOnlyList<FeatureEditHookRejection> Rejections);

public static partial class FeatureEditGate
{
    // Run after input validation and snapshot reads, before owner and editor stamps. Only calculated
    // fields join the write: the full snapshot a constraint reads never becomes a replacement update.
    public static FeatureEditPreparation ApplyAttributeRules(FeatureEditCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.Resource.AttributeRules.Count == 0)
        {
            return new(check.Batch, []);
        }

        var replacements = new Dictionary<(FeatureEditOperationKind, int), Feature>();
        var rejected = new List<FeatureEditHookRejection>();
        foreach (var row in FeatureEditRows.Enumerate(check.Batch))
        {
            var attributes = check.EffectiveAttributes?.Invoke(row) ?? row.Feature?.Attributes ?? ImmutableDictionary<string, object?>.Empty;
            var result = AttributeRuleEngine.Apply(check.Resource, attributes, Event(row.Kind), check.UnsupportedExpressions);
            if (!result.IsValid)
            {
                rejected.Add(new(row.Kind, row.Index, result.Violations[0].Message));
            }
            else if (row.Feature is { } feature && result.CalculatedFields.Count > 0)
            {
                replacements[(row.Kind, row.Index)] = WithCalculatedFields(feature, result);
            }
        }

        return new(ReplaceCalculatedFeatures(check.Batch, replacements), rejected);
    }

    private static Feature WithCalculatedFields(Feature feature, AttributeRuleApplicationResult result)
    {
        var attributes = feature.Attributes.ToBuilder();
        var calculated = result.CalculatedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in calculated)
        {
            attributes[field] = result.Attributes[field];
        }

        return feature with
        {
            Attributes = attributes.ToImmutable(),
            ExplicitAttributeRemovals = feature.ExplicitAttributeRemovals.IsDefaultOrEmpty
                ? feature.ExplicitAttributeRemovals
                : [.. feature.ExplicitAttributeRemovals.Where(name => !calculated.Contains(name))],
        };
    }

    private static FeatureEditBatch ReplaceCalculatedFeatures(
        FeatureEditBatch batch,
        Dictionary<(FeatureEditOperationKind, int), Feature> replacements)
    {
        if (replacements.Count == 0)
        {
            return batch;
        }

        var rows = FeatureEditRows.Enumerate(batch);
        return batch with
        {
            Creates = Replace(batch.Creates, FeatureEditOperationKind.Create, replacements),
            Updates = Replace(batch.Updates, FeatureEditOperationKind.Update, replacements),
            Operations = batch.Operations.IsDefaultOrEmpty ? batch.Operations :
            [
                .. batch.Operations.Select((operation, position) =>
                    replacements.TryGetValue((rows[position].Kind, rows[position].Index), out var feature)
                        ? operation with { Feature = feature }
                        : operation),
            ],
        };
    }

    private static ImmutableArray<Feature> Replace(
        ImmutableArray<Feature> features,
        FeatureEditOperationKind kind,
        Dictionary<(FeatureEditOperationKind, int), Feature> replacements)
        => features.IsDefaultOrEmpty ? features :
            [.. features.Select((feature, index) => replacements.GetValueOrDefault((kind, index), feature))];

    private static AttributeRuleEditEvent Event(FeatureEditOperationKind kind) => kind switch
    {
        FeatureEditOperationKind.Create => AttributeRuleEditEvent.Insert,
        FeatureEditOperationKind.Update => AttributeRuleEditEvent.Update,
        _ => AttributeRuleEditEvent.Delete,
    };
}
