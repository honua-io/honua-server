// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Server.Features.Portal.Sharing;

internal static class PortalSharingTables
{
    internal static string Qualify(string table, string? schema)
    {
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(string.IsNullOrWhiteSpace(schema) ? "honua" : schema.Trim())
            + "." + builder.QuoteIdentifier(table);
    }
}
