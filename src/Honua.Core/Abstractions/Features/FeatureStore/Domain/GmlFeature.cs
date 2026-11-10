// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct GmlFeature
{
    public required long Id { get; init; }

    public required string? GeometryGml { get; init; }

    public required ImmutableDictionary<string, object?> Attributes { get; init; }

    public static GmlFeature Create(long id, string? geometryGml, ImmutableDictionary<string, object?> attributes)
        => new() { Id = id, GeometryGml = geometryGml, Attributes = attributes };
}
