// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct RawGeoServicesFeature
{
    public required long Id { get; init; }

    public string? PublicIdJson { get; init; }

    public string? AttributesJson { get; init; }

    public double? X { get; init; }

    public double? Y { get; init; }

    public static RawGeoServicesFeature Create(long id, string? attributesJson, double? x, double? y)
        => new() { Id = id, AttributesJson = attributesJson, X = x, Y = y };

    public static RawGeoServicesFeature Create(long id, string? publicIdJson, string? attributesJson, double? x, double? y)
        => new() { Id = id, PublicIdJson = publicIdJson, AttributesJson = attributesJson, X = x, Y = y };
}
