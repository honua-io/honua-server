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

    // The caller and instant for one ApplyEdits call, in the gRPC stored timestamp forms.
    public static EditorTrackingStamp CallerStamp(string? principalName, DateTimeOffset utcNow)
        => new(principalName, field => TimeValue(field, utcNow));

    public static FeatureEditBatch Stamp(MetadataV2Object resource, FeatureEditBatch batch, EditorTrackingStamp stamp)
    {
        if (resource.Editing is null)
        {
            return batch;
        }

        return batch with
        {
            Creates = Map(batch.Creates, feature => StampFeature(feature, attributes =>
                EditorTrackingFields.StampInsert(resource, attributes, stamp))),
            Updates = Map(batch.Updates, feature => StampFeature(feature, attributes =>
                EditorTrackingFields.StampUpdate(resource, attributes, stamp))),
        };
    }

    // Time-capable types only. Other types, including a 32-bit integer that cannot hold epoch
    // milliseconds, stay unstamped rather than receiving a value nothing validates. An undeclared
    // field takes the DateTime form. Exact timestamps are stored as canonical text after EnsureStorable.
    private static object? TimeValue(MetadataV2Field? field, DateTimeOffset utcNow) => field?.Type switch
    {
        null or MetadataV2FieldType.DateTime => utcNow.UtcDateTime,
        MetadataV2FieldType.Date => GrpcTypedValues.FormatDate(DateOnly.FromDateTime(utcNow.UtcDateTime)),
        MetadataV2FieldType.String or MetadataV2FieldType.TimestampOffset
            => utcNow.ToString("O", CultureInfo.InvariantCulture),
        MetadataV2FieldType.BigInteger or MetadataV2FieldType.Double => utcNow.ToUnixTimeMilliseconds(),
        _ => null,
    };

    private static Feature StampFeature(Feature feature, Action<IDictionary<string, object?>> stamp)
    {
        var attributes = feature.Attributes.ToBuilder();
        stamp(attributes);
        return feature with { Attributes = attributes.ToImmutable() };
    }

    private static Feature Omit(Feature feature, HashSet<string> names)
    {
        var kept = feature.Attributes.RemoveRange(feature.Attributes.Keys.Where(names.Contains));
        return kept.Count == feature.Attributes.Count ? feature : feature with { Attributes = kept };
    }

    private static ImmutableArray<Feature> Map(ImmutableArray<Feature> features, Func<Feature, Feature> map)
        => features.IsDefaultOrEmpty ? features : features.Select(map).ToImmutableArray();
}
