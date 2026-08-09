// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.DuckDB.Features.FeatureStore.Services;

/// <summary>
/// Shared constants for internal column names used across DuckDB query building, execution, and result processing.
/// </summary>
internal static class DuckDBQueryEncoding
{
    internal const string InternalTotalCountColumn = "__honua_total_count";
}
