// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Npgsql;

namespace Honua.TestKit.Infrastructure;

// Fixture hosts skip startup migrations, but layer publishing runs the production migration
// runner against its data connection. Pointed at the shared fixture database, an empty
// public.schema_versions makes that run replay every script in one transaction, whose ALTERs on
// the global honua tables deadlock with other live hosts' background services. Journaling the
// shared database once, before a host starts on it, leaves those runs the no-op they are against
// a migrated production system database. Each fixture schema owns its attachment family, so the
// replayed honua copy is dropped: two attachment authorities make every feature delete fail.
internal static class SharedDatabaseProductionJournal
{
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly HashSet<string> _journaledDatabases = new(StringComparer.Ordinal);

    internal static async Task EnsureJournaledAsync(PostgresFixture postgres)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_journaledDatabases.Contains(postgres.ConnectionString))
            {
                return;
            }

            await postgres.RunUnderSchemaMutationLockAsync(ApplyProductionMigrationsAsync(postgres.ConnectionString))
                .ConfigureAwait(false);
            _journaledDatabases.Add(postgres.ConnectionString);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Func<Task> ApplyProductionMigrationsAsync(string connectionString) => async () =>
    {
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest);
        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly).ConfigureAwait(false);
        if (!result.Successful)
        {
            // A PostgresException is rethrown as-is so the schema-mutation lock retries a deadlock victim.
            throw result.Error as PostgresException
                ?? (Exception)new InvalidOperationException(
                    $"Journaling the shared fixture database failed: {result.ErrorMessage}",
                    result.Error);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var drop = new NpgsqlCommand(
            "DROP TABLE IF EXISTS honua.import_attachment_cleanup, honua.attachments", connection);
        await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
    };
}
