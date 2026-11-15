// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Protocols.Grpc;

// Applies the shared editor-tracking rule to a gRPC edit batch: client values are dropped before
// validation, and stamps use the stored form of the matching gRPC wire value. An update leaves the
// creator and created-at fields to the partial-update writer, which keeps the stored values.
internal static class GrpcEditorTracking
{
    public static FeatureEditBatch OmitClientValues(MetadataV2Object resource, FeatureEditBatch batch)
    {
        var names = new HashSet<string>(EditorTrackingFields.Names(resource.Editing), StringComparer.OrdinalIgnoreCase);
        return names.Count == 0
            ? batch
            : batch with
            {
                Creates = Map(batch.Creates, feature => Omit(feature, names)),
                Updates = Map(batch.Updates, feature => Omit(feature, names)),
            };
    }

    public static FeatureEditBatch Stamp(
        MetadataV2Object resource,
        FeatureEditBatch batch,
        string? principalName,
        DateTimeOffset utcNow)
    {
        if (resource.Editing is null)
        {
            return batch;
        }

        TrackingTimeValue timeValue = (MetadataV2Field? field, out object? value) => TryTimeValue(field?.Type, utcNow, out value);
        return batch with
        {
            Creates = Map(batch.Creates, feature => StampFeature(feature, isInsert: true)),
            Updates = Map(batch.Updates, feature => StampFeature(feature, isInsert: false)),
        };

        Feature StampFeature(Feature feature, bool isInsert)
        {
            var attributes = feature.Attributes.ToBuilder();
            EditorTrackingFields.Stamp(resource, attributes, isInsert, principalName, timeValue);
            return feature with { Attributes = attributes.ToImmutable() };
        }
    }

    // A 32-bit integer cannot hold epoch milliseconds, so such a field is left unstamped rather
    // than failing every write. Exact timestamps are stored as canonical text after EnsureStorable.
    private static bool TryTimeValue(MetadataV2FieldType? type, DateTimeOffset utcNow, out object? value)
    {
        value = type switch
        {
            MetadataV2FieldType.Integer => null,
            MetadataV2FieldType.Date => GrpcTypedValues.FormatDate(DateOnly.FromDateTime(utcNow.UtcDateTime)),
            MetadataV2FieldType.String or MetadataV2FieldType.TimestampOffset
                => utcNow.ToString("O", CultureInfo.InvariantCulture),
            MetadataV2FieldType.BigInteger or MetadataV2FieldType.Double => utcNow.ToUnixTimeMilliseconds(),
            _ => utcNow.UtcDateTime,
        };
        return value is not null;
    }

    private static Feature Omit(Feature feature, HashSet<string> names)
    {
        var kept = feature.Attributes.RemoveRange(feature.Attributes.Keys.Where(names.Contains));
        return kept.Count == feature.Attributes.Count ? feature : feature with { Attributes = kept };
    }

    private static ImmutableArray<Feature> Map(ImmutableArray<Feature> features, Func<Feature, Feature> map)
        => features.IsDefaultOrEmpty ? features : features.Select(map).ToImmutableArray();
}
