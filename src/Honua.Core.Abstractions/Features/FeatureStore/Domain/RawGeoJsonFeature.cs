// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct RawGeoJsonFeature
{
    public required long Id { get; init; }

    public required string? GeometryGeoJson { get; init; }

    public string? PublicIdJson { get; init; }

    public required string? PropertiesJson { get; init; }

    public static RawGeoJsonFeature Create(
        long id,
        string? geometryGeoJson,
        string? propertiesJson)
        => new() { Id = id, GeometryGeoJson = geometryGeoJson, PropertiesJson = propertiesJson };

    public static RawGeoJsonFeature Create(
        long id,
        string? geometryGeoJson,
        string? publicIdJson,
        string? propertiesJson)
        => new()
        {
            Id = id,
            GeometryGeoJson = geometryGeoJson,
            PublicIdJson = publicIdJson,
            PropertiesJson = propertiesJson
        };
}
