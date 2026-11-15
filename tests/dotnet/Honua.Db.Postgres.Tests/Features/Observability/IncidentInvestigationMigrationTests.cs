// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Observability;

[Collection("Database")]
public sealed class IncidentInvestigationMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "193_AllowIncidentInvestigationReferences.sql";

    [IntegrationTest]
    public async Task Migration_RepeatedApplication_PreservesOldAndIncidentReferencesAcrossConnections()
    {
        await WithSchemaAsync(async schema =>
        {
            await InsertReferencesAsync(schema, (4, 7));
            await ApplyMigrationAsync(schema, Migration);
            await InsertReferencesAsync(schema, (5, 8));
            await ApplyMigrationAsync(schema, Migration);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand($"""
                SELECT (SELECT array_agg(resource_kind ORDER BY resource_kind)
                    FROM "{schema}".investigation_links),
                    (SELECT array_agg(event_kind ORDER BY event_kind)
                    FROM "{schema}".investigation_pins)
                """, connection);
            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetFieldValue<short[]>(0).Should().Equal(4, 5);
            reader.GetFieldValue<short[]>(1).Should().Equal(7, 8);
        });
    }

    [IntegrationTest]
    public async Task OriginalMigration_RejectsIncidentReferences()
    {
        await WithSchemaAsync(async schema =>
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertReferencesAsync(schema, (5, 8)));
            exception.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        });
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 9)]
    [Trait("Category", "Integration")]
    public async Task Migration_RejectsUndefinedReferenceKinds(int linkKind, int pinKind)
    {
        await WithSchemaAsync(async schema =>
        {
            await ApplyMigrationAsync(schema, Migration);
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertReferencesAsync(schema, (linkKind, pinKind)));
            exception.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        });
    }

    private Task InsertReferencesAsync(string schema, (int Link, int Pin) kinds) => ExecuteAsync(schema, $"""
        INSERT INTO $HonuaSchema$.investigation_links
            (investigation_id, resource_kind, resource_id, created_by)
            VALUES ('inv_test', {kinds.Link}, 'incident:platform:stable-episode', 'operator');
        INSERT INTO $HonuaSchema$.investigation_pins
            (investigation_id, event_kind, event_ref, occurred_at, created_by)
            VALUES ('inv_test', {kinds.Pin}, 'incident:platform:stable-episode:7',
                '2026-11-14T00:00:00Z', 'operator');
        """);

    private async Task WithSchemaAsync(Func<string, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(IncidentInvestigationMigrationTests));
        try
        {
            await ExecuteAsync(schema, """
                CREATE TABLE $HonuaSchema$.alert_events (event_id BIGINT PRIMARY KEY, occurred_at TIMESTAMPTZ);
                CREATE TABLE $HonuaSchema$.audit_log (audit_id TEXT PRIMARY KEY, timestamp TIMESTAMPTZ);
                """);
            await ApplyMigrationAsync(schema, "034_CreateOperateObservability.sql");
            await ExecuteAsync(schema, """
                INSERT INTO $HonuaSchema$.investigations (investigation_id, title, created_by)
                    VALUES ('inv_test', 'Alarm evidence', 'operator');
                """);
            await test(schema);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema, string migration)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
        {
            root = root.Parent;
        }

        var path = Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", migration);
        var sql = (await File.ReadAllTextAsync(path)).Replace("honua.", "$HonuaSchema$.", StringComparison.Ordinal);
        await ExecuteAsync(schema, sql);
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }
}
