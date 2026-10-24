// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Provider-option keys recognized by the SQL Server feature provider when reading
/// <see cref="Honua.Core.Features.Catalog.Domain.LayerStorageMapping.ProviderOptions"/>.
/// </summary>
internal static class SqlServerProviderOptions
{
    /// <summary>
    /// Selects between SQL Server <c>geometry</c> (planar) and <c>geography</c> (geodetic) columns.
    /// Accepts <c>geometry</c> (default) or <c>geography</c>. The choice affects distance units
    /// and which spatial method overloads are valid.
    /// </summary>
    public const string GeometryType = "geometryType";
}
