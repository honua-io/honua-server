// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private static async Task<List<ColumnInfo>> ResolvePublishableColumnsAsync(
        NpgsqlConnection connection,
        TableInfo table,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        const string sql = """
            SELECT obj_description(to_regclass(format('%I.%I', @schema, @table)), 'pg_class')
                   = 'honua:import-owned:v1';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("schema", table.Schema);
        command.Parameters.AddWithValue("table", table.Table);
        var owned = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (owned is not true)
        {
            return table.Columns;
        }

        // These columns describe importer storage, not customer attributes.
        return table.Columns.Where(column => column.Name is not ("properties" or "created_at")).ToList();
    }
}
