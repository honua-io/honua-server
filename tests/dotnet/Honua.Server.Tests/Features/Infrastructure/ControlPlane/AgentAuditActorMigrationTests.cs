// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class AgentAuditActorMigrationTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task AgentMigration_Replayed_PreservesLegacyActorsAndAppendOnlyRules()
    {
        var schema = await database.CreateIsolatedSchemaAsync(nameof(AgentAuditActorMigrationTests));
        try
        {
            await ApplyAsync("033_CreateAuditLog.sql", schema);
            foreach (var actorType in new[] { "Anonymous", "UserId", "ApiKey", "System" })
            {
                await InsertAsync(schema, actorType);
            }

            await ApplyAsync("168_AddAgentAuditActor.sql", schema);
            await ApplyAsync("168_AddAgentAuditActor.sql", schema);
            await InsertAsync(schema, "Agent");
            await database.ExecuteAsync($"UPDATE \"{schema}\".audit_log SET actor = 'changed'; DELETE FROM \"{schema}\".audit_log");
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"SELECT actor_type, actor FROM \"{schema}\".audit_log ORDER BY audit_id", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var actors = new List<string>();
            while (await reader.ReadAsync())
            {
                reader.GetString(1).Should().Be("stable-principal");
                actors.Add(reader.GetString(0));
            }

            actors.Should().Equal("Anonymous", "UserId", "ApiKey", "System", "Agent");
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
            var insert = () => InsertAsync(schema, "Unknown");
            var failure = await insert.Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            failure.Which.ConstraintName.Should().Be("chk_audit_log_actor_type");
        }
        finally
        {
            await database.DropSchemaAsync(schema);
        }
    }

    private Task InsertAsync(string schema, string actorType)
        => database.ExecuteAsync($"""
            INSERT INTO "{schema}".audit_log
                (timestamp, event_type, actor, actor_type, resource_type, action, outcome, correlation_id)
            VALUES (now(), 'AdminAction', 'stable-principal', '{actorType}', 'test', 'test', 'Success', 'migration-test')
            """);

    private async Task ApplyAsync(string suffix, string schema)
    {
        var assembly = typeof(Program).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var sql = (await reader.ReadToEndAsync())
            .Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal)
            .Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
        await database.ExecuteDdlUnderLockAsync(sql, schema);
    }
}
