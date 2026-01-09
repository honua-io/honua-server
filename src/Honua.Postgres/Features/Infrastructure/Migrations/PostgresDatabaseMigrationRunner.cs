// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using DbUp;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Migrations;

internal sealed class PostgresDatabaseMigrationRunner : IDatabaseMigrationRunner
{
    public Task<DatabaseMigrationResult> RunMigrationsAsync(
        string connectionString,
        Assembly migrationsAssembly,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        ArgumentNullException.ThrowIfNull(migrationsAssembly);
        cancellationToken.ThrowIfCancellationRequested();

        var migrationConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "public",
        }.ConnectionString;

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(migrationConnectionString)
            .JournalToPostgresqlTable("public", "schema_versions")
            .WithScriptsEmbeddedInAssembly(migrationsAssembly)
            .WithTransaction()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();
        var appliedScripts = result.Scripts.Select(script => script.Name).ToArray();

        if (!result.Successful)
        {
            var error = result.Error ?? new InvalidOperationException("Database migration failed.");
            return Task.FromResult(DatabaseMigrationResult.Failed(error, error.Message, appliedScripts));
        }

        return Task.FromResult(DatabaseMigrationResult.Succeeded(appliedScripts));
    }
}
