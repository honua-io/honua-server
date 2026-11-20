// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Db.DuckDB.Features.Infrastructure;

internal sealed record DuckDBExternalSourcePlan(
    int LayerId,
    string ViewName,
    string CreateViewSql,
    ImmutableArray<string> RequiredExtensions)
{
    // Paths the view reads; the connection lockdown allows exactly these.
    public ImmutableArray<string> SourcePaths { get; init; } = [];
}
