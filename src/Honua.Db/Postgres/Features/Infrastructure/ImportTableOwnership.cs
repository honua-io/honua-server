// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Core.Features.Import.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>Proves importer ownership before destructive table operations.</summary>
internal static class ImportTableOwnership
{
    internal const string Marker = "honua:import-owned:v1";

    internal static string MarkSql(string schema, string table)
        => $"COMMENT ON TABLE {SqlIdentifier.QuoteIdentifier(schema)}.{SqlIdentifier.QuoteIdentifier(table)} IS '{Marker}';";

    // Call within the same transaction as the subsequent DROP/rename. The lock
    // prevents another session replacing the checked relation before destruction.
    internal static async Task<bool> AssertOwnedAsync(NpgsqlConnection connection, string schema, string table,
        CancellationToken cancellationToken)
    {
        RefuseReservedStagedTarget(schema, table);
        return await AssertMarkedAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);
    }

    // A server-derived staged table belongs to its staged import: other importers and sinks may not
    // create, append to or replace it. Create-only promotion and staged discard are the only writers.
    internal static void RefuseReservedStagedTarget(string schema, string table)
    {
        if (StagedImportTableNames.IsReservedPhysical(table))
        {
            throw new ImportTableOwnershipException(schema, table);
        }
    }

    // Staged discard is the one caller allowed to destroy a server-derived staged table.
    internal static Task<bool> AssertOwnedStagedAsync(NpgsqlConnection connection, string schema, string table,
        CancellationToken cancellationToken)
        => StagedImportTableNames.IsReservedPhysical(table)
            ? AssertMarkedAsync(connection, schema, table, cancellationToken)
            : throw new ImportTableOwnershipException(schema, table);

    private static async Task<bool> AssertMarkedAsync(NpgsqlConnection connection, string schema, string table,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(connection, schema, table, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using var guard = new NpgsqlCommand(
            $"LOCK TABLE {SqlIdentifier.QuoteIdentifier(schema)}.{SqlIdentifier.QuoteIdentifier(table)} IN EXCLUSIVE MODE; " +
            "SELECT obj_description(to_regclass(@relation), 'pg_class')", connection);
        guard.Parameters.AddWithValue("relation", $"{SqlIdentifier.QuoteIdentifier(schema)}.{SqlIdentifier.QuoteIdentifier(table)}");
        if (await guard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string marker || marker != Marker)
        {
            throw new ImportTableOwnershipException(schema, table);
        }

        return true;
    }

    internal static async Task AssertExistingOwnedAsync(NpgsqlConnection connection, string schema, string table,
        CancellationToken cancellationToken)
    {
        if (!await AssertOwnedAsync(connection, schema, table, cancellationToken).ConfigureAwait(false))
        {
            throw new ImportTableOwnershipException(schema, table);
        }
    }

    internal static async Task<bool> ExistsAsync(NpgsqlConnection connection, string schema, string table,
        CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@relation) IS NOT NULL", connection);
        exists.Parameters.AddWithValue("relation", $"{SqlIdentifier.QuoteIdentifier(schema)}.{SqlIdentifier.QuoteIdentifier(table)}");
        return await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
