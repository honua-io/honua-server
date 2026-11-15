// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class AgentAuditActorMigrationTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [Fact]
    public async Task AgentMigration_AddsAgentPrincipalWithoutChangingActorType()
    {
        var sql = await ReadMigrationAsync("168_AddAgentAuditActor.sql");
        sql.Should().Contain("ADD COLUMN IF NOT EXISTS actor_agent").And.NotContain("DROP CONSTRAINT");
        MigrationSafetyClassifier.Classify("168_AddAgentAuditActor.sql", sql).Classification
            .Should().Be(MigrationSafetyClassification.Expand);
        MigrationReaderCompatibility.Declare("168_AddAgentAuditActor.sql", sql).Classification
            .Should().Be(MigrationReaderCompatibilityClassification.Expand);
    }

    [IntegrationTest]
    public async Task AgentMigration_Replayed_PreservesLegacyActorsAndAppendOnlyRules()
    {
        var schema = await database.CreateIsolatedSchemaAsync(nameof(AgentAuditActorMigrationTests));
        try
        {
            await SeedLegacyActorsAsync(schema);
            await ApplyAsync("168_AddAgentAuditActor.sql", schema);
            await ApplyAsync("168_AddAgentAuditActor.sql", schema);
            await database.ExecuteAsync($"UPDATE \"{schema}\".audit_log SET actor = 'changed'; DELETE FROM \"{schema}\".audit_log");
            await InsertAsync(schema, new AuditRow("ApiKey", "entry-agent", "agent-1"));
            (await ReadActorsAsync(schema)).Should().Equal(
                ("Anonymous", "entry-Anonymous", (string?)null),
                ("UserId", "entry-UserId", null),
                ("ApiKey", "entry-ApiKey", null),
                ("System", "entry-System", null),
                ("ApiKey", "entry-agent", "agent-1"));
        }
        finally
        {
            await database.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AgentMigration_RejectsUnknownActorTypes()
    {
        var schema = await database.CreateIsolatedSchemaAsync(nameof(AgentAuditActorMigrationTests));
        try
        {
            await ApplyAsync("033_CreateAuditLog.sql", schema);
            await ApplyAsync("168_AddAgentAuditActor.sql", schema);
            foreach (var actorType in new[] { "Unknown", "Agent" })
            {
                var insert = () => InsertAsync(schema, new AuditRow(actorType));
                var failure = await insert.Should().ThrowAsync<PostgresException>();
                failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
                failure.Which.ConstraintName.Should().Be("chk_audit_log_actor_type");
            }
        }
        finally
        {
            await database.DropSchemaAsync(schema);
        }
    }

    private readonly record struct AuditRow(string ActorType, string? EntryHash = null, string? ActorAgent = null);

    private async Task SeedLegacyActorsAsync(string schema)
    {
        await ApplyAsync("033_CreateAuditLog.sql", schema);
        await ApplyAsync("069_AddAuditLogHashChain.sql", schema);
        foreach (var actorType in new[] { "Anonymous", "UserId", "ApiKey", "System" })
        {
            await InsertAsync(schema, new AuditRow(actorType, $"entry-{actorType}"));
        }
    }

    private async Task<List<(string Type, string Hash, string? Agent)>> ReadActorsAsync(string schema)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT actor_type, actor, prev_hash, entry_hash, actor_agent FROM \"{schema}\".audit_log ORDER BY audit_id",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var actors = new List<(string Type, string Hash, string? Agent)>();
        while (await reader.ReadAsync())
        {
            reader.GetString(1).Should().Be("stable-principal");
            reader.IsDBNull(2).Should().BeTrue();
            actors.Add((
                reader.GetString(0),
                reader.GetString(3).TrimEnd(),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return actors;
    }

    private Task InsertAsync(string schema, AuditRow row)
    {
        var extraColumns = "";
        var extraValues = "";
        if (row.EntryHash is not null)
        {
            extraColumns += ", prev_hash, entry_hash";
            extraValues += ", NULL, '" + row.EntryHash + "'";
        }

        if (row.ActorAgent is not null)
        {
            extraColumns += ", actor_agent";
            extraValues += ", '" + row.ActorAgent + "'";
        }

        return database.ExecuteAsync($"""
            INSERT INTO "{schema}".audit_log
                (timestamp, event_type, actor, actor_type, resource_type, action, outcome, correlation_id{extraColumns})
            VALUES (now(), 'AdminAction', 'stable-principal', '{row.ActorType}', 'test', 'test', 'Success', 'migration-test'{extraValues})
            """);
    }

    private async Task ApplyAsync(string suffix, string schema)
    {
        var sql = (await ReadMigrationAsync(suffix))
            .Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal);
        await database.ExecuteDdlUnderLockAsync(sql, schema);
    }

    private static async Task<string> ReadMigrationAsync(string suffix)
    {
        var assembly = typeof(Program).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
