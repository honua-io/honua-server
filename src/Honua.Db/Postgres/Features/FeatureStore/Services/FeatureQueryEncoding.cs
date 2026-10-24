// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static class FeatureQueryEncoding
{
    public const string GeometryColumn = "geometry";
    public const string InternalDistanceColumn = "__honua_distance";
    public const string InternalTotalCountColumn = "__honua_total_count";
    public const string PublicDistanceColumn = "distance";
    public const string PublicTotalCountColumn = "total_count";
    public const int GeometryTextPrecision = 15;
}
