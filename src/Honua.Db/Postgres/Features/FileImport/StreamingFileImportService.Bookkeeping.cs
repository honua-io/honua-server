// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static async Task MarkImportBookkeepingAsync(
        NpgsqlTransaction transaction, (string Schema, string Table) target, CancellationToken cancellationToken)
    {
        var qualifiedTable = $"{SqlIdentifier.QuoteIdentifier(target.Schema)}.{SqlIdentifier.QuoteIdentifier(target.Table)}";
        var sql = $"""
            COMMENT ON COLUMN {qualifiedTable}.properties IS 'honua:import-bookkeeping:v1';
            COMMENT ON COLUMN {qualifiedTable}.created_at IS 'honua:import-bookkeeping:v1';
            """;
        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
