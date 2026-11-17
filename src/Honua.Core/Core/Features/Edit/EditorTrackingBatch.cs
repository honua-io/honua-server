// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

// Protocols omit client stamps before validation and stamp the prepared batch after calculations.
public static class EditorTrackingBatch
{
    public static ImmutableDictionary<string, object?> OmitClientValues(
        MetadataV2Object resource, ImmutableDictionary<string, object?> attributes)
    {
        var names = EditorTrackingFields.Names(resource.Editing).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return attributes.RemoveRange(attributes.Keys.Where(names.Contains));
    }

    public static FeatureEditBatch OmitClientValues(MetadataV2Object resource, FeatureEditBatch batch)
        => resource.Editing is null ? batch : Map(batch, (feature, insert) =>
        {
            var names = EditorTrackingFields.Names(resource.Editing).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preserved = feature.PreservedAttributeNames.IsDefault ? [] : feature.PreservedAttributeNames;
            if (!insert)
            {
                preserved = [.. preserved.Concat(new[] { resource.Editing.CreatorField, resource.Editing.CreatedAtField }
                    .OfType<string>().Where(static name => !string.IsNullOrWhiteSpace(name)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
            }

            return feature with
            {
                Attributes = OmitClientValues(resource, feature.Attributes),
                PreservedAttributeNames = preserved,
                ExplicitAttributeRemovals = feature.ExplicitAttributeRemovals.IsDefault ? default
                    : [.. feature.ExplicitAttributeRemovals.Where(name => !names.Contains(name))],
            };
        });

    public static FeatureEditBatch Stamp(MetadataV2Object resource, FeatureEditBatch batch, EditorTrackingStamp stamp)
        => resource.Editing is null ? batch : Map(batch, (feature, insert) =>
        {
            var attributes = OmitClientValues(resource, feature.Attributes).ToBuilder();
            if (insert)
            {
                EditorTrackingFields.StampInsert(resource, attributes, stamp);
            }
            else
            {
                EditorTrackingFields.StampUpdate(resource, attributes, stamp);
            }

            return feature with { Attributes = attributes.ToImmutable() };
        });

    private static FeatureEditBatch Map(FeatureEditBatch batch, Func<Feature, bool, Feature> map)
        => batch with
        {
            Creates = batch.Creates.IsDefaultOrEmpty ? batch.Creates : [.. batch.Creates.Select(feature => map(feature, true))],
            Updates = batch.Updates.IsDefaultOrEmpty ? batch.Updates : [.. batch.Updates.Select(feature => map(feature, false))],
            Operations = batch.Operations.IsDefaultOrEmpty ? batch.Operations : [.. batch.Operations.Select(operation =>
                operation.Feature is { } feature && operation.Kind != FeatureEditOperationKind.Delete
                    ? operation with { Feature = map(feature, operation.Kind == FeatureEditOperationKind.Create) }
                    : operation)],
        };
}
