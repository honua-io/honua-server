// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Studio writers share this schema-scoped transaction barrier. Reviewed shared-style
/// mutations take it exclusively before row locks, keeping the derived consumer set
/// stable through authorization and commit without serializing ordinary Studio edits.
/// </summary>
internal static class StudioStyleDependencyBarrier
{
    internal static string Key(string? schemaName) => SchemaSearchPath.QualifyTable("studio_style_dependency_barrier", schemaName);

    internal static async Task AcquireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string key, bool exclusive, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        var sql = exclusive ? "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))"
            : "SELECT pg_advisory_xact_lock_shared(hashtextextended(@key, 0))";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(bounded.Token).ConfigureAwait(false);
    }
}
