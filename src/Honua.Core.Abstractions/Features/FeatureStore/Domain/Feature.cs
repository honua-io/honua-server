// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct Feature
{
    public required long Id { get; init; }

    public long? ObjectId
    {
        readonly get => Id;
        init => Id = value ?? 0;
    }

    public required byte[]? Geometry { get; init; }

    public required ImmutableDictionary<string, object?> Attributes { get; init; }

    // The unmasked concurrency token is never part of a serialized feature response.
    [JsonIgnore]
    public string? ReadStateToken { get; init; }

    // A partial edit preserves omitted attributes that field security hid.
    [JsonIgnore]
    public bool PreserveOmittedMaskedAttributes { get; init; }

    // Names in this set must not be restored from the masked read snapshot.
    [JsonIgnore]
    public ImmutableArray<string> ExplicitAttributeRemovals { get; init; }

    [JsonIgnore]
    public bool PreserveOmittedGeometry { get; init; }

    public static Feature Create(long id, byte[]? geometry, ImmutableDictionary<string, object?> attributes)
        => new() { Id = id, Geometry = geometry, Attributes = attributes };

    public static Feature Create(long id, byte[]? geometry)
        => new() { Id = id, Geometry = geometry, Attributes = ImmutableDictionary<string, object?>.Empty };
}
