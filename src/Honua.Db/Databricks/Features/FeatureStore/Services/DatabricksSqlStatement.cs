// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Databricks.Features.FeatureStore.Services;

internal sealed record DatabricksSqlStatement(string Sql, IReadOnlyList<DatabricksSqlParameter> Parameters)
{
    public static DatabricksSqlStatement WithoutParameters(string sql) => new(sql, []);
}

internal sealed record DatabricksSqlParameter(string Name, string? Value, string? Type = null);
