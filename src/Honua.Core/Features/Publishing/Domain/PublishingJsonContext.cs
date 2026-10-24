// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>
/// AOT-compatible JSON serialization metadata for durable publishing records.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters =
    [
        typeof(JsonStringEnumConverter<PublishSourceKind>),
        typeof(JsonStringEnumConverter<PublishTargetKind>),
        typeof(JsonStringEnumConverter<PublishedServiceStatus>),
        typeof(JsonStringEnumConverter<RefreshMode>),
        typeof(JsonStringEnumConverter<ArtifactKind>)
    ])]
[JsonSerializable(typeof(PublishedServiceRecord))]
[JsonSerializable(typeof(IReadOnlyList<PublishedServiceRecord>))]
public sealed partial class PublishingJsonContext : JsonSerializerContext
{
}
