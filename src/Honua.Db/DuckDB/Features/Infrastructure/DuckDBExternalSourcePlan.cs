// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Db.DuckDB.Features.Infrastructure;

/// <summary>
/// Planned per-connection temporary view for a DuckDB external file source.
/// </summary>
internal sealed record DuckDBExternalSourcePlan(
    int LayerId,
    string ViewName,
    string CreateViewSql,
    ImmutableArray<string> RequiredExtensions);
