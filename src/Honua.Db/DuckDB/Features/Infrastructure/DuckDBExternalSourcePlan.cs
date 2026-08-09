// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// Planned per-connection temporary view for a DuckDB external file source.
/// </summary>
internal sealed record DuckDBExternalSourcePlan(
    int LayerId,
    string ViewName,
    string CreateViewSql,
    ImmutableArray<string> RequiredExtensions);
