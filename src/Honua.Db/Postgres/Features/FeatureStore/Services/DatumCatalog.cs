// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static class DatumCatalog
{
    internal static readonly IDatumTransformationCatalog Shared = GeoServicesDatumTransformationCatalog.Create();
}
