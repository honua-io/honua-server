// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using DbUp;
using DbUp.Engine;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Migrations;

internal sealed class PostgresDatabaseMigrationRunner : IDatabaseMigrationRunner
{
    private const long MigrationLockKey = 8_044_282_257_919_950_151;
    private const string SafeMigrationFailureMessage = "Database migration failed.";
    private static readonly TimeSpan _migrationLockWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _migrationLockRetryDelay = TimeSpan.FromSeconds(1);

    public Task<DatabaseMigrationPlan> PlanMigrationsAsync(
        string connectionString,
        Assembly migrationsAssembly,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateArguments(connectionString, migrationsAssembly);
            cancellationToken.ThrowIfCancellationRequested();

            var upgrader = BuildUpgrader(BuildMigrationConnectionString(connectionString), migrationsAssembly);
            var pendingScripts = upgrader.GetScriptsToExecute().Select(script => script.Name).ToArray();
            var executedButNotDiscoveredScripts = upgrader.GetExecutedButNotDiscoveredScripts().ToArray();

            return Task.FromResult(DatabaseMigrationPlan.Succeeded(pendingScripts, executedButNotDiscoveredScripts));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DatabaseMigrationPlan.Failed(ex, SafeMigrationFailureMessage));
        }
    }

    public async Task<DatabaseMigrationResult> RunMigrationsAsync(
        string connectionString,
        Assembly migrationsAssembly,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(connectionString, migrationsAssembly);
        cancellationToken.ThrowIfCancellationRequested();

        var migrationConnectionString = BuildMigrationConnectionString(connectionString);

        await using var lockConnection = new NpgsqlConnection(migrationConnectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var lockAcquired = await TryAcquireMigrationLockAsync(lockConnection, cancellationToken).ConfigureAwait(false);
        if (!lockAcquired)
        {
            var error = new TimeoutException(
                $"Timed out waiting {_migrationLockWaitTimeout.TotalMinutes:F0} minute(s) for database migration lock.");
            return DatabaseMigrationResult.Failed(error, error.Message);
        }

        var upgrader = BuildUpgrader(migrationConnectionString, migrationsAssembly);

        try
        {
            var result = upgrader.PerformUpgrade();
            var appliedScripts = result.Scripts.Select(script => script.Name).ToArray();

            if (!result.Successful)
            {
                var error = result.Error ?? new InvalidOperationException("Database migration failed.");
                return DatabaseMigrationResult.Failed(error, SafeMigrationFailureMessage, appliedScripts);
            }

            return DatabaseMigrationResult.Succeeded(appliedScripts);
        }
        finally
        {
            await ReleaseMigrationLockAsync(lockConnection).ConfigureAwait(false);
        }
    }

    private static void ValidateArguments(string connectionString, Assembly migrationsAssembly)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        ArgumentNullException.ThrowIfNull(migrationsAssembly);
    }

    private static string BuildMigrationConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "public",
        }.ConnectionString;

    private static UpgradeEngine BuildUpgrader(string connectionString, Assembly migrationsAssembly) =>
        DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .JournalToPostgresqlTable("public", "schema_versions")
            .WithScriptsEmbeddedInAssembly(migrationsAssembly)
            .WithTransaction()
            .LogToConsole()
            .Build();

    private static async Task<bool> TryAcquireMigrationLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(_migrationLockWaitTimeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lock_key);", connection);
            _ = command.Parameters.AddWithValue("lock_key", MigrationLockKey);

            var acquired = (bool?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false;
            if (acquired)
            {
                return true;
            }

            await Task.Delay(_migrationLockRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task ReleaseMigrationLockAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_key);", connection);
            _ = command.Parameters.AddWithValue("lock_key", MigrationLockKey);
            _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
        }
        catch
        {
            // The advisory lock is connection-scoped and will be released when the connection is disposed.
        }
    }
}
