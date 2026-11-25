// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal static partial class GrpcConversionHelpers
{
    internal sealed class ProtoFeatureConversionOptions
    {
        public bool IncludeGeometry { get; init; } = true;
        public GeometryLimits? GeometryLimits { get; init; }
        public string? ObjectIdFieldName { get; init; }
        public IReadOnlySet<string>? HiddenFields { get; init; }
        public IReadOnlyDictionary<string, MetadataV2FieldType>? ExactFieldTypes { get; init; }
    }

    internal static Proto.Feature ToProtoFeatureWithOptions(
        Feature feature,
        ProtoFeatureConversionOptions options)
    {
        var proto = new Proto.Feature { Id = feature.Id };

        foreach (var (key, value) in feature.Attributes)
        {
            if (FeatureAttributeVisibility.IsInternalAttribute(key) || options.HiddenFields?.Contains(key) == true)
            {
                continue;
            }

            if (options.ObjectIdFieldName is { Length: > 0 } && IsRepeatedObjectId(key, value, feature.Id, options.ObjectIdFieldName))
            {
                continue;
            }

            MetadataV2FieldType? fieldType = options.ExactFieldTypes is not null && options.ExactFieldTypes.TryGetValue(key, out var resolvedType)
                ? resolvedType
                : null;
            proto.Attributes[key] = ToAttributeValue(value, fieldType);
        }

        if (options.IncludeGeometry && feature.Geometry is { Length: > 0 })
        {
            var geometry = ToProtoGeometry(feature.Geometry, options.GeometryLimits);
            if (geometry.ShapeCase != Proto.Geometry.ShapeOneofCase.None)
            {
                proto.Geometry = geometry;
            }
        }

        return proto;
    }
}
