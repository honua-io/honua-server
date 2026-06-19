// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Snowflake.Features.FeatureStore.Services;

/// <summary>
/// Provider-option keys recognized by the Snowflake feature provider when reading
/// <see cref="Honua.Core.Features.Catalog.Domain.LayerStorageMapping.ProviderOptions"/>.
/// </summary>
internal static class SnowflakeProviderOptions
{
    /// <summary>
    /// Selects between Snowflake <c>GEOGRAPHY</c> (geodetic, WGS84 spherical) and <c>GEOMETRY</c>
    /// (planar, projected) columns. Accepts <c>geography</c> (default) or <c>geometry</c>. The
    /// choice affects which spatial constructor (<c>TO_GEOGRAPHY</c> vs <c>TO_GEOMETRY</c>) is
    /// used to materialize filter geometries.
    /// </summary>
    public const string GeometryType = "geometryType";
}
