// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>Proves importer ownership before destructive table operations.</summary>
internal static class ImportTableOwnership
{
    internal const string Marker = "honua:import-owned:v1";

    internal static string MarkSql(string schema, string table)
        => $"COMMENT ON TABLE {Quote(schema)}.{Quote(table)} IS '{Marker}';";

    // Call within the same transaction as the subsequent DROP/rename. The lock
    // prevents another session replacing the checked relation before destruction.
    internal static async Task<bool> AssertOwnedAsync(NpgsqlConnection connection, string schema, string table,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(connection, schema, table, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using var guard = new NpgsqlCommand(
            $"LOCK TABLE {Quote(schema)}.{Quote(table)} IN EXCLUSIVE MODE; " +
            "SELECT obj_description(to_regclass(@relation), 'pg_class')", connection);
        guard.Parameters.AddWithValue("relation", $"{Quote(schema)}.{Quote(table)}");
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
        exists.Parameters.AddWithValue("relation", $"{Quote(schema)}.{Quote(table)}");
        return await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

internal sealed class ImportTableOwnershipException(string schema, string table)
    : InvalidOperationException($"IMPORT_TARGET_NOT_OWNED: Table '{schema}.{table}' is not owned by the importer; it was not modified. Choose a new target or ask the database administrator to explicitly adopt a verified legacy import.")
{
}
