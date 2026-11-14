// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static async Task<string> CreateFreshStagingAsync(
        NpgsqlTransaction transaction, (string Schema, int Srid) target, CancellationToken cancellationToken)
    {
        var name = $"stage_{Guid.NewGuid():N}__staging";
        // Strict CREATE refuses collisions; no existing dataset is reused or removed.
        const string formatSql = """
            SELECT format('CREATE SCHEMA IF NOT EXISTS %I; '
                'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY CHECK (ST_SRID(geometry) = %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW()); '
                'COMMENT ON TABLE %I.%I IS %L; '
                'CREATE INDEX %I ON %I.%I USING GIST (geometry); '
                'CREATE INDEX %I ON %I.%I USING GIN (properties);',
                @schema, @schema, @name, @srid, @schema, @name, 'honua:import-owned:v1',
                'idx_' || @name || '_geometry', @schema, @name,
                'idx_' || @name || '_properties', @schema, @name)
            """;
        await using var format = new NpgsqlCommand(formatSql, transaction.Connection!, transaction);
        format.Parameters.AddWithValue("schema", target.Schema);
        format.Parameters.AddWithValue("name", name);
        format.Parameters.AddWithValue("srid", target.Srid);
        var sql = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
        await using var create = new NpgsqlCommand(sql, transaction.Connection!, transaction);
        await create.ExecuteNonQueryAsync(cancellationToken);
        return name;
    }

    // Create-only owns a uniquely named staging table and drops only that verified table.
    // Replace and staged append keep the shared sibling cleanup, which also reclaims its sequence and indexes.
    private static Task DropLoadStagingAsync(NpgsqlConnection connection,
        (ImportLoadMode Mode, string Schema, string Table, string Staging) tables, CancellationToken cancellationToken)
        => tables.Mode == ImportLoadMode.CreateOnly
            ? DiscardStagingAsync(connection, (tables.Schema, tables.Staging), cancellationToken)
            : DropStagingTableAsync(connection, tables.Schema, tables.Table, cancellationToken);

    private static async Task DiscardStagingAsync(
        NpgsqlConnection connection, (string Schema, string Staging) tables, CancellationToken cancellationToken)
    {
        // The ownership check locks the table, so the check and the drop share one transaction.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ImportTableOwnership.AssertOwnedAsync(connection, tables.Schema, tables.Staging, cancellationToken))
        {
            return;
        }

        const string formatSql = "SELECT format('DROP TABLE %I.%I', @schema, @staging)";
        await using var format = new NpgsqlCommand(formatSql, connection, transaction);
        format.Parameters.AddWithValue("schema", tables.Schema);
        format.Parameters.AddWithValue("staging", tables.Staging);
        var sql = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
        await using var drop = new NpgsqlCommand(sql, connection, transaction);
        await drop.ExecuteNonQueryAsync(cancellationToken);
        // Callers clean up with CancellationToken.None; never abandon a half-finished discard.
        await transaction.CommitAsync(CancellationToken.None);
    }
}
