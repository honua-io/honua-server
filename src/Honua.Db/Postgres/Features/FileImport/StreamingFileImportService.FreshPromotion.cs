// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static async Task PromoteFreshStagingTableAsync(
        NpgsqlTransaction transaction, StagingPromotion promotion, CancellationToken cancellationToken)
    {
        await ImportTableOwnership.AssertExistingOwnedAsync(
            transaction.Connection!, promotion.Schema, promotion.StagingTable, cancellationToken);
        // Rename without DROP: a target created after the absence check must cause a
        // collision, even when its creator does not take the import advisory lock.
        // PostgreSQL format(%I) quotes every identifier, including canonical index names.
        // The key and sequence keep the staging table's unique stem: target-derived names can belong to a sibling.
        const string renameSql = """
            SELECT format(
                'ALTER TABLE %I.%I RENAME TO %I; '
                'ALTER INDEX IF EXISTS %I.%I RENAME TO %I; '
                'ALTER INDEX IF EXISTS %I.%I RENAME TO %I; '
                'ALTER INDEX IF EXISTS %I.%I RENAME TO %I; '
                'ALTER SEQUENCE IF EXISTS %I.%I RENAME TO %I;',
                @schema, @staging, @target,
                @schema, 'idx_' || @staging || '_geometry', honua.import_index_name(@target, 'geometry'),
                @schema, 'idx_' || @staging || '_properties', honua.import_index_name(@target, 'properties'),
                @schema, @staging || '_pkey', left(@staging, -length('__staging')) || '_pkey',
                @schema, @staging || '_id_seq', left(@staging, -length('__staging')) || '_id_seq')
            """;
        await using var format = new NpgsqlCommand(renameSql, transaction.Connection!, transaction);
        format.Parameters.AddWithValue("schema", promotion.Schema);
        format.Parameters.AddWithValue("staging", promotion.StagingTable);
        format.Parameters.AddWithValue("target", promotion.Table);
        var sql = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
        await using var rename = new NpgsqlCommand(sql, transaction.Connection!, transaction);
        await rename.ExecuteNonQueryAsync(cancellationToken);
    }
}
