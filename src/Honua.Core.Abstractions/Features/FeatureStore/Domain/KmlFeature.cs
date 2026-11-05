// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct KmlFeature
{
    public required long Id { get; init; }

    public required string? GeometryKml { get; init; }

    public required ImmutableDictionary<string, object?> Attributes { get; init; }

    public static KmlFeature Create(long id, string? geometryKml, ImmutableDictionary<string, object?> attributes)
        => new() { Id = id, GeometryKml = geometryKml, Attributes = attributes };
}
