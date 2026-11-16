// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.TestKit.Mixins;

internal static class WebAppFixtureSecureConnectionMixin
{
    internal const string TestSecureConnectionName = "test";

    internal const string TestSecureConnectionCreatedBy = "test-fixture";

    private static readonly SemaphoreSlim _secureConnectionLock = new(1, 1);

    internal static async Task<Guid?> GetTestSecureConnectionIdAsync(IServiceScope? scope)
    {
        if (scope is null)
        {
            return null;
        }

        var registry = scope.ServiceProvider.GetService<ISecureConnectionRegistry>();
        if (registry == null)
        {
            return null;
        }

        var connection = await registry.GetConnectionByNameAsync(TestSecureConnectionName).ConfigureAwait(false);
        return connection?.ConnectionId;
    }

    internal static async Task EnsureTestSecureConnectionAsync(IServiceScope? scope)
    {
        if (scope is null)
        {
            return;
        }

        var services = scope.ServiceProvider;
        var connectionProvider = services.GetRequiredService<IAdoNetDatabaseConnectionProvider>();

        if (!await SecureConnectionTablesAvailableAsync(connectionProvider).ConfigureAwait(false))
        {
            return;
        }

        await EnsureSecureConnectionProviderColumnAsync(connectionProvider).ConfigureAwait(false);

        var registry = services.GetRequiredService<ISecureConnectionRegistry>();
        var encryptionService = services.GetRequiredService<IConnectionEncryptionService>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await _secureConnectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await registry.GetConnectionByNameAsync(TestSecureConnectionName).ConfigureAwait(false);
            if (existing != null)
            {
                return;
            }

            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Host) ||
                string.IsNullOrWhiteSpace(builder.Database) ||
                string.IsNullOrWhiteSpace(builder.Username) ||
                builder.Port <= 0)
            {
                return;
            }

            var encrypted = await encryptionService.EncryptConnectionStringAsync(connectionString).ConfigureAwait(false);
            var keyVersion = await encryptionService.GetCurrentKeyVersionAsync().ConfigureAwait(false);
            var sslRequired = builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull;
            var sslMode = Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true);

            var connection = DataConnection.CreateWithEncryptedCredentials(
                name: TestSecureConnectionName,
                host: builder.Host,
                port: builder.Port,
                databaseName: builder.Database,
                username: builder.Username,
                encryptedConnectionString: encrypted,
                encryptionKeyVersion: keyVersion,
                createdBy: TestSecureConnectionCreatedBy,
                description: "Test secure connection",
                sslRequired: sslRequired,
                sslMode: sslMode);

            try
            {
                await registry.CreateConnectionAsync(connection).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
            {
                // Another fixture created the same connection concurrently.
            }
        }
        finally
        {
            _secureConnectionLock.Release();
        }
    }

    private static async Task<bool> SecureConnectionTablesAvailableAsync(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        const string sql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'honua'
              AND table_name = 'data_connections'
            LIMIT 1
            """;
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = await connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 10;
                var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                return result != null && result != DBNull.Value;
            }
            catch (Exception ex) when (IsTransientSecureConnectionCheckFailure(ex))
            {
                if (attempt == maxAttempts)
                {
                    Console.Error.WriteLine($"WARNING: Could not verify secure-connection table after {maxAttempts} attempts. Proceeding without it.");
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine($"WARNING: Could not verify secure-connection table after {maxAttempts} attempts. Proceeding without it.");
        return false;
    }

    private static async Task EnsureSecureConnectionProviderColumnAsync(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        // honua-server#1568 (signature 2 — 40P01: deadlock detected): WebAppFixture runs this on
        // every InitializeAsync, and the ALTER targets the literal, process-global
        // honua.data_connections table (the per-test search_path isolation does not scope it).
        // Under the parallel [Collection("Database")] shards, dozens of fixtures issue this
        // ACCESS EXCLUSIVE ALTER against the same global table concurrently and deadlock against
        // each other (and against the catalog ALTERs the seeds apply). Serialize it on the same
        // SeedRunner advisory lock every other global-honua mutation now uses, in one transaction,
        // with a bounded retry on the benign deadlock/serialization victim (the ALTER is
        // idempotent — ADD COLUMN IF NOT EXISTS — so re-running is safe).
        const string sql = """
            ALTER TABLE IF EXISTS honua.data_connections
                ADD COLUMN IF NOT EXISTS provider_name TEXT NOT NULL DEFAULT 'postgis';
            """;

        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            await using var connection = await connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                await using (var lockCommand = connection.CreateCommand())
                {
                    lockCommand.Transaction = transaction;
                    // SeedApplicationLockKey is an internal compile-time constant (not user input),
                    // so it is inlined as a literal — the provider exposes a base DbCommand whose
                    // parameter collection has no AddWithValue helper.
                    lockCommand.CommandText =
                        $"SELECT pg_advisory_xact_lock({SeedRunner.SeedApplicationLockKey.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
                    lockCommand.CommandTimeout = 30;
                    _ = await lockCommand.ExecuteScalarAsync().ConfigureAwait(false);
                }

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.CommandTimeout = 10;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await transaction.CommitAsync().ConfigureAwait(false);
                return;
            }
            catch (PostgresException ex)
                when (attempt < maxAttempts &&
                      ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                // Deadlock/serialization victim: the transaction is fully rolled back (including
                // the advisory xact lock), so retrying the idempotent ALTER is safe.
                await transaction.RollbackAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientSecureConnectionCheckFailure(Exception ex)
    {
        return ex is TimeoutException or TaskCanceledException or NpgsqlException;
    }
}
