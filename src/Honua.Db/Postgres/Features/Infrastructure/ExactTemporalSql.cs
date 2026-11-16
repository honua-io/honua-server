// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Infrastructure;

internal static class ExactTemporalSql
{
    internal static string Timestamp(string text)
    {
        // PostgreSQL timestamps retain six fractional digits; separate the exact
        // fraction before converting the whole-second timestamp to an instant.
        var seconds = $"EXTRACT(EPOCH FROM regexp_replace({text}, '[.][0-9]+', '')::timestamptz)";
        var fraction = $"COALESCE(('0.' || substring({text} from '[.]([0-9]+)'))::numeric, 0)";
        return $"({seconds} + {fraction})";
    }

    internal static string Time(string text)
        => $"(split_part({text}, ':', 1)::numeric * 3600 + " +
           $"split_part({text}, ':', 2)::numeric * 60 + split_part({text}, ':', 3)::numeric)";
}
