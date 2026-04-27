// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// SQL command used during connection bootstrap with a sanitized operational description.
/// </summary>
internal readonly record struct DuckDBBootstrapSqlCommand(string Sql, string Description);
