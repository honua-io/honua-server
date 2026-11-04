// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.DuckDB.Features.FeatureStore.Services;

/// <summary>
/// Shared constants for internal column names used across DuckDB query building, execution, and result processing.
/// </summary>
internal static class DuckDBQueryEncoding
{
    internal const string InternalTotalCountColumn = "__honua_total_count";
}
