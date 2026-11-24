// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Share;

internal static class PostgresShareStoreHelpers
{
    // Only connectivity/capacity/auth SQLSTATE classes (and transient errors) count as
    // "store unavailable"; other PostgresExceptions (constraint violations, SQL defects)
    // propagate so they surface as server errors instead of retryable outages.
    public static bool IsStoreUnavailable(NpgsqlException exception)
    {
        if (exception is not PostgresException postgres)
        {
            return true;
        }

        return postgres.IsTransient
            || postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
            || postgres.SqlState.StartsWith("53", StringComparison.Ordinal)
            || postgres.SqlState.StartsWith("57", StringComparison.Ordinal)
            || postgres.SqlState.StartsWith("28", StringComparison.Ordinal);
    }
}
