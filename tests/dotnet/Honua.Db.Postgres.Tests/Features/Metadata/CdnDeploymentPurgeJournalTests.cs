// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

[Collection("Database")]
public sealed class CdnDeploymentPurgeJournalTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public Task RecordCompletion_DuplicateDeployment_RejectsReplay() => InSchemaAsync(async connection =>
    {
        await RecordAsync(connection, ("service-a", "deployment-a", "event-a"));
        var duplicate = () => RecordAsync(connection, ("service-a", "deployment-a", "event-b"));
        await duplicate.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.UniqueViolation);
    });

    [IntegrationTest]
    public Task RecordCompletion_DuplicateEvent_RejectsReplay() => InSchemaAsync(async connection =>
    {
        await RecordAsync(connection, ("service-a", "deployment-a", "event-a"));
        var duplicate = () => RecordAsync(connection, ("service-a", "deployment-b", "event-a"));
        await duplicate.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.UniqueViolation);
    });

    [IntegrationTest]
    public Task RecordCompletion_AnotherService_PreservesIndependentCompletion() => InSchemaAsync(async connection =>
    {
        await RecordAsync(connection, ("service-a", "deployment-a", "event-a"));
        await RecordAsync(connection, ("service-b", "deployment-a", "event-a"));
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM cdn_deployment_purges
             WHERE task_definition_arn = 'task-definition-a' AND purge_completed_at <= clock_timestamp()
            """, connection);
        Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(2);
    });

    [IntegrationTest]
    public Task RecordCompletion_TransactionAborts_DoesNotSuppressRetry() => InSchemaAsync(async connection =>
    {
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await RecordAsync(connection, ("service-a", "deployment-a", "event-a"));
            await transaction.RollbackAsync();
        }

        await RecordAsync(connection, ("service-a", "deployment-a", "event-a"));
    });

    private async Task InSchemaAsync(Func<NpgsqlConnection, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(CdnDeploymentPurgeJournalTests));
        try
        {
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "src", "Honua.Server", "Migrations", "163_CreateCdnDeploymentPurges.sql"));
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using (var command = new NpgsqlCommand(
                sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal), connection))
            {
                await command.ExecuteNonQueryAsync();
            }

            await using (var command = new NpgsqlCommand($"SET search_path TO \"{schema}\"", connection))
            {
                await command.ExecuteNonQueryAsync();
            }

            await test(connection);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task RecordAsync(NpgsqlConnection connection, (string Service, string Deployment, string EventId) record)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO cdn_deployment_purges
                (cluster_arn, service_arn, deployment_id, task_definition_arn, event_id)
            VALUES ('cluster-a', @service, @deployment, 'task-definition-a', @event)
            """, connection);
        command.Parameters.AddWithValue("service", record.Service);
        command.Parameters.AddWithValue("deployment", record.Deployment);
        command.Parameters.AddWithValue("event", record.EventId);
        await command.ExecuteNonQueryAsync();
    }
}
