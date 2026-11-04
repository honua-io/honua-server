// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.DuckDB.Features.Infrastructure;

/// <summary>
/// SQL command used during connection bootstrap with a sanitized operational description.
/// </summary>
internal readonly record struct DuckDBBootstrapSqlCommand(string Sql, string Description);
