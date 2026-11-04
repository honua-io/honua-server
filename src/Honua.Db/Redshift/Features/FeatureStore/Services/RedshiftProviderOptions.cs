// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Redshift.Features.FeatureStore.Services;

/// <summary>
/// Provider-option keys recognized by the Redshift feature provider when reading
/// <see cref="Honua.Core.Features.Catalog.Domain.LayerStorageMapping.ProviderOptions"/>.
/// </summary>
internal static class RedshiftProviderOptions
{
    /// <summary>
    /// Selects between Redshift <c>GEOMETRY</c> (planar) and <c>GEOGRAPHY</c> (geodetic) columns.
    /// Accepts <c>geometry</c> (default) or <c>geography</c>. The choice affects distance units
    /// and which spatial function overloads are valid.
    /// </summary>
    public const string GeometryType = "geometryType";
}
