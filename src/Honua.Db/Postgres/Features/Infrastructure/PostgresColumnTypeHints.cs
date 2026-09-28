// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Publication hints used to select optional query plans. They are not authoritative
/// after external DDL; the reader must verify the physical type before using them.
/// </summary>
internal static class PostgresColumnTypeHints
{
    internal const string SmallintColumnPrefix = "postgresSmallintColumn:";
}
