// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Transactions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Metadata;

// A public copy can outlive its worker and the catalog revision that produced it.
// Preserve catalog and policy state until origin and edge cleanup is complete.
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

    internal static async Task PrepareWriteAsync(
        IAdoNetDatabaseConnectionProvider provider, string? schemaName, CancellationToken cancellationToken)
    {
        // Use an owned, unenlisted connection: rejecting the caller's mutation must
        // not roll back the cleanup request. Do not acquire the advisory fence here;
        // an audited caller may already own it. Promotion checks the row state, and
        // the actual mutation still acquires the fence and checks for late copies.
        using var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        await using var owner = await provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = owner.RequireNpgsqlConnection();
        var table = SchemaSearchPath.QualifyTable("public_publication_generations", schemaName);
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection);
        exists.Parameters.AddWithValue("table", table);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return;
        }

        await using var request = new NpgsqlCommand(
            $"UPDATE {table} SET state = 'revoking' WHERE state <> 'revoked'", connection);
        if (await request.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0)
        {
            throw CleanupRequired();
        }
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
        await using var copies = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {table} WHERE state <> 'revoked')",
            transaction.Connection, transaction);
        if (await copies.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
        {
            throw CleanupRequired();
        }
    }
    private static InvalidOperationException CleanupRequired() => new(
        "Public publication copies require completed revocation before catalog or read-policy changes. " +
        "Cleanup obligations have been retained; invalidation submission alone is insufficient.");
}
