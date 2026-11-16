// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Metadata;

// A public copy can outlive its worker and the catalog revision that produced it.
// Until a completed-revocation implementation is installed, preserve both the
// catalog and its cleanup obligations rather than acknowledging an unsafe edit.
internal static class PublicPublicationWriteFence
{
    internal const long LockKey = 144047715;

    internal static async Task LockAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@key)", transaction.Connection, transaction);
        command.Parameters.AddWithValue("key", LockKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RequireNoPublicCopiesAsync(
        NpgsqlTransaction transaction, string? schemaName, CancellationToken cancellationToken)
    {
        var table = SchemaSearchPath.QualifyTable("public_publication_generations", schemaName);
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL",
            transaction.Connection, transaction);
        exists.Parameters.AddWithValue("table", table);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return;
        }

        // Policies span catalog environments; checking all rows is deliberately
        // conservative. Pending rows matter as much as advertised generations.
        await using var copies = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {table})",
            transaction.Connection, transaction);
        if (await copies.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
        {
            throw new InvalidOperationException(
                "Public publication copies require completed revocation before catalog or read-policy changes. " +
                "Cleanup obligations have been retained; invalidation submission alone is insufficient.");
        }
    }
}
