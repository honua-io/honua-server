// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class RealtimeNotificationTriggerTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task Jobs_StatusChanges_AppendOnlyPublicFieldsInStableOrder()
    {
        await using var harness = await CreateHarnessAsync();
        await InsertJobsAsync(harness);
        (await harness.ReadSequencesAsync("jobs")).Should().Equal(1, 2);
        await harness.ExecuteAsync($"UPDATE {harness.Table("execution_jobs")} SET percent_complete = 50");
        (await harness.ReadSequencesAsync("jobs")).Should().Equal(1, 2);
        await harness.ExecuteAsync($"UPDATE {harness.Table("execution_jobs")} SET status = 'Running', version = 2");

        (await harness.ScalarAsync<string>($"""
            SELECT string_agg(scope_key || ':' || (payload->>'status'), ',' ORDER BY seq)
            FROM {harness.Table("realtime_events")} WHERE family = 'jobs'
            """)).Should().Be("job-a:Queued,job-b:Queued,job-a:Running,job-b:Running");
        (await harness.ScalarAsync<bool>($"""
            SELECT bool_and(kind = 'job.status' AND payload->>'requesterId' = 'alice'
                AND payload->>'kind' = 'analysis' AND payload->>'jobId' = scope_key
                AND (SELECT count(*) FROM jsonb_object_keys(payload)) = 5
                AND payload ? 'updatedAt')
            FROM {harness.Table("realtime_events")} WHERE family = 'jobs'
            """)).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Catalog_EtagChanges_AppendPreviousEtagWithoutDocument()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ExecuteAsync($"""
            INSERT INTO {harness.Table("metadata_v2_snapshots")}
                (environment, revision, schema_version, api_version, document, etag, generated_at)
            VALUES ('dev', 1, '2', '1', jsonb_build_object('secret', 'hidden'), 'v1', now()),
                   ('dev', 2, '2', '1', jsonb_build_object('secret', 'hidden'), 'v2', now());
            INSERT INTO {harness.Table("metadata_v2_current")} (environment, revision, etag)
                VALUES ('dev', 1, 'v1');
            UPDATE {harness.Table("metadata_v2_current")} SET activated_at = now();
            UPDATE {harness.Table("metadata_v2_current")} SET revision = 2, etag = 'v2';
            """);

        (await harness.ReadSequencesAsync("catalog")).Should().Equal(1, 2);
        (await harness.ScalarAsync<bool>($"""
            SELECT bool_and(scope_key = 'dev' AND kind = 'catalog.changed'
                AND payload->>'environment' = 'dev'
                AND (SELECT count(*) FROM jsonb_object_keys(payload)) = 3)
            FROM {harness.Table("realtime_events")} WHERE family = 'catalog'
            """)).Should().BeTrue();
        (await harness.ScalarAsync<string>($"""
            SELECT string_agg(coalesce(payload->>'previousEtag', 'null') || '>' || (payload->>'etag'), ',' ORDER BY seq)
            FROM {harness.Table("realtime_events")} WHERE family = 'catalog'
            """)).Should().Be("null>v1,v1>v2");
    }

    [IntegrationTest]
    public async Task Jobs_Rollback_RollsBackEventsAndHead()
    {
        await using var harness = await CreateHarnessAsync();
        await InsertJobsAsync(harness);
        await using (var connection = await harness.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {harness.Table("execution_jobs")} SET status = 'Failed'";
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        (await harness.ReadSequencesAsync("jobs")).Should().Equal(1, 2);
        (await harness.ScalarAsync<long>($"SELECT head FROM {harness.Table("realtime_log_heads")} WHERE family = 'jobs'"))
            .Should().Be(2);
        (await harness.ScalarAsync<bool>($"SELECT bool_and(status = 'Queued') FROM {harness.Table("execution_jobs")}"))
            .Should().BeTrue();
    }

    private async Task<RealtimeLogHarness> CreateHarnessAsync()
    {
        var harness = await RealtimeLogHarness.CreateAsync(database);
        await harness.ApplyMigrationAsync("Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql");
        await harness.ApplyMigrationAsync("Honua.Server.Migrations.159_CreateRealtimeJobCatalogTriggers.sql");
        return harness;
    }

    private static Task InsertJobsAsync(RealtimeLogHarness harness) => harness.ExecuteAsync($"""
        INSERT INTO {harness.Table("execution_jobs")}
            (operation_id, version, status, attempt_count, updated_at, expires_at, record, kind,
             runtime_profile, backend, priority, created_at, cancellation_requested, heartbeat_timeout,
             max_duration, lookup_keys)
        SELECT id, 1, 'Queued', 0, now(), now() + interval '1 day',
            json_build_object('audit', json_build_object('requestedBy', 'alice'), 'spec', json_build_object('secret', 'hidden'), 'result', 'hidden'),
            'analysis', 'default', 'local', 0, now(), false, interval '1 minute', interval '1 hour', ARRAY[]::text[]
        FROM (VALUES ('job-b'), ('job-a')) AS jobs(id)
        """);
}
