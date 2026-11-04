// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Shared embedded datum catalog for draw paths that do not receive a request-scoped
/// selection. The host registers the same table as a singleton; this instance is the
/// same data so tile, scene, and identify SQL stay on it when a caller did not.
/// </summary>
internal static class DatumCatalog
{
    internal static readonly IDatumTransformationCatalog Shared = GeoServicesDatumTransformationCatalog.Create();
}
