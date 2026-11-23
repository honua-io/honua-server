// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.CloudIntegration.Tests;

// Shared by the LocalSubstrate migration-runner tests: the default enforcing runner and the
// read-back queries they assert with.
internal static class LocalSubstrateMigrationDatabase
{
    public static PostgresDatabaseMigrationRunner CreateEnforcingRunner()
        => new(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest,
            Options.Create(new MigrationSafetyOptions { Enforce = true }));

    public static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@qualified) IS NOT NULL";
        command.Parameters.AddWithValue("qualified", $"public.{table}");
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
