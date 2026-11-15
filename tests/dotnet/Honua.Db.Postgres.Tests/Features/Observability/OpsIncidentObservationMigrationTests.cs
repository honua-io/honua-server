// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Observability;

[Collection("Database")]
public sealed class OpsIncidentObservationMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_RepeatedApplication_PreservesEvidenceAndScopedIdentity()
    {
        await WithSchemaAsync(async schema =>
        {
            await InsertAsync(schema, new());
            await InsertAsync(schema, new() { Authority = "other-authority" });
            await ApplyMigrationAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand($"""
                SELECT authority_scope, evidence, observed_at
                FROM "{schema}".ops_incident_observations ORDER BY authority_scope
                """, connection);
            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("authority");
            reader.GetString(1).Should().Be("{\"original\":true}");
            reader.GetFieldValue<DateTimeOffset>(2).Should().Be(DateTimeOffset.UnixEpoch);
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("other-authority");
            (await reader.ReadAsync()).Should().BeFalse();
        });
    }

    [IntegrationTest]
    public async Task Migration_DuplicateObservation_RejectsEvidenceReplacement()
    {
        await WithSchemaAsync(async schema =>
        {
            await InsertAsync(schema, new());
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertAsync(schema, new() { Evidence = "{}" }));
            error.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        });
    }

    [IntegrationTest]
    public async Task Migration_InvalidIdentityStateOrEvidence_RejectsInvalidRows()
    {
        await WithSchemaAsync(async schema =>
        {
            foreach (var invalid in InvalidRows())
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(schema, invalid));
                error.SqlState.Should().BeOneOf(PostgresErrorCodes.CheckViolation, PostgresErrorCodes.InvalidTextRepresentation);
            }
            await InsertAsync(schema, new() { Authority = new string('é', 128), State = 0 });
            await InsertAsync(schema, new() { Authority = "second", State = 2 });
        });
    }

    private static IEnumerable<ObservationRow> InvalidRows() =>
    [
        new() { Authority = " " }, new() { Authority = new string('é', 129) },
        new() { Source = "" }, new() { Source = new string('a', 257) },
        new() { Id = " " }, new() { Id = new string('a', 257) },
        new() { Finding = "" }, new() { Finding = new string('a', 257) },
        new() { State = -1 }, new() { State = 3 },
        new() { Evidence = "broken-json" }, new() { Evidence = "\"" + new string('a', 65535) + "\"" }
    ];

    private async Task WithSchemaAsync(Func<string, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(OpsIncidentObservationMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema);
            await action(schema);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var path = Path.Join(AppContext.BaseDirectory, "Migrations", "169_CreateOpsIncidentObservations.sql");
        var sql = (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertAsync(string schema, ObservationRow row)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{schema}".ops_incident_observations
                (authority_scope, source_id, observation_id, finding_id, observed_at, state, evidence)
            VALUES (@authority, @source, @id, @finding, @observed, @state, @evidence)
            """, connection);
        command.Parameters.AddWithValue("authority", row.Authority);
        command.Parameters.AddWithValue("source", row.Source);
        command.Parameters.AddWithValue("id", row.Id);
        command.Parameters.AddWithValue("finding", row.Finding);
        command.Parameters.AddWithValue("observed", DateTimeOffset.UnixEpoch);
        command.Parameters.AddWithValue("state", row.State);
        command.Parameters.AddWithValue("evidence", row.Evidence);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ObservationRow
    {
        public string Authority { get; init; } = "authority";
        public string Source { get; init; } = "source";
        public string Id { get; init; } = "observation";
        public string Finding { get; init; } = "finding";
        public int State { get; init; } = 1;
        public string Evidence { get; init; } = "{\"original\":true}";
    }
}
