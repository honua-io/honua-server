// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Publication hints used to select optional query plans. They are not authoritative
/// after external DDL; the reader must verify the physical type before using them.
/// </summary>
internal static class PostgresColumnTypeHints
{
    internal const string SmallintColumnPrefix = "postgresSmallintColumn:";
}
