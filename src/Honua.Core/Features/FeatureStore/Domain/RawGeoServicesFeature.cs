// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Raw point feature payload used by GeoServices REST JSON fast paths.
/// </summary>
public readonly record struct RawGeoServicesFeature(
    long Id,
    string? AttributesJson,
    double? X,
    double? Y)
{
    public static RawGeoServicesFeature Create(long id, string? attributesJson, double? x, double? y)
        => new(id, attributesJson, x, y);
}
