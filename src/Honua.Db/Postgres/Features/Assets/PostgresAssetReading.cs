// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Db.Postgres.Features.Assets;

internal static class PostgresAssetReading
{
    // Lists read limit + 1 rows: the extra row only says whether another page exists.
    public static bool TrimToPage<T>(List<T> rows, int limit)
    {
        if (rows.Count <= limit)
        {
            return false;
        }

        rows.RemoveRange(limit, rows.Count - limit);
        return true;
    }

    // Boxing a nullable struct through (object?) trips CA1508's flow analysis; unwrap it explicitly.
    public static object DbValue<T>(T? value)
        where T : struct
        => value.HasValue ? value.Value : DBNull.Value;

    public static string? OptionalString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static DateTimeOffset RequiredTime(DbDataReader reader, int ordinal) => new(reader.GetDateTime(ordinal), TimeSpan.Zero);

    public static DateTimeOffset? OptionalTime(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : RequiredTime(reader, ordinal);
}
