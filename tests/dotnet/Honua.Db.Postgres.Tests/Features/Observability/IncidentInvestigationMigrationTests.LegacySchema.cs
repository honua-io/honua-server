// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Observability;

public sealed partial class IncidentInvestigationMigrationTests
{
    [IntegrationTest]
    public async Task Migration_NonDefaultConfiguredSchema_PreservesTheUnmodifiedLegacyFamily()
    {
        var database = "incident_upgrade_" + Guid.NewGuid().ToString("N");
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\" TEMPLATE template0", admin))
        {
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            await VerifyLegacyUpgradeAsync(database);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private async Task VerifyLegacyUpgradeAsync(string database)
    {
        var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database, Pooling = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }
        var legacy = await File.ReadAllTextAsync(Path.Join(directory!.FullName,
            "src/Honua.Server/Migrations/034_CreateOperateObservability.sql"));
        await using var setup = new NpgsqlCommand("""
            CREATE SCHEMA honua;
            CREATE SCHEMA configured;
            CREATE TABLE honua.alert_events (event_id BIGINT PRIMARY KEY, occurred_at TIMESTAMPTZ);
            CREATE TABLE honua.audit_log (audit_id TEXT PRIMARY KEY, timestamp TIMESTAMPTZ);
            """ + legacy, connection);
        await setup.ExecuteNonQueryAsync();
        var migration = (await ReadMigrationAsync(Migration)).Replace("$HonuaSchema$", "\"configured\"", StringComparison.Ordinal);
        await using var upgrade = new NpgsqlCommand(migration + migration, connection);
        await upgrade.ExecuteNonQueryAsync();
        await using var check = new NpgsqlCommand("""
            SELECT to_regclass('honua.investigation_incident_links') IS NOT NULL
                AND to_regclass('honua.investigation_incident_pins') IS NOT NULL
                AND to_regclass('configured.investigations') IS NULL
            """, connection);
        (await check.ExecuteScalarAsync()).Should().Be(true);
    }
}
