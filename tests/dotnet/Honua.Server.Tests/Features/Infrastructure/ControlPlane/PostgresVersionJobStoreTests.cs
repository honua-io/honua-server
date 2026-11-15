// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresVersionJobStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task GetAsync_OnAnotherStore_ReturnsTheJobLastSaved()
    {
        await using var harness = await CreateHarnessAsync();
        var pending = Job();
        var succeeded = pending with
        {
            Status = VersionJobStatus.Succeeded,
            StartedAt = pending.CreatedAt.AddSeconds(1),
            CompletedAt = pending.CreatedAt.AddSeconds(2),
            ConflictCount = 3,
            AppliedChanges = 42,
            CanPost = true,
        };
        await harness.CreateVersionJobStore().SaveAsync(pending);
        await harness.CreateVersionJobStore().SaveAsync(succeeded);

        (await harness.CreateVersionJobStore().GetAsync(pending.JobId)).Should().Be(succeeded);
        (await harness.CreateVersionJobStore().GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    [IntegrationTest]
    public async Task SaveAsync_BackwardTransition_IsIgnored()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateVersionJobStore();
        var pending = Job();
        var running = pending with { Status = VersionJobStatus.Running, StartedAt = pending.CreatedAt.AddSeconds(1) };
        var failed = running with { Status = VersionJobStatus.Failed, CompletedAt = pending.CreatedAt.AddSeconds(2), ErrorMessage = "failed" };
        await store.SaveAsync(pending);
        await store.SaveAsync(running);
        await store.SaveAsync(failed);

        await store.SaveAsync(running);
        await store.SaveAsync(pending);
        await store.SaveAsync(failed with { Status = VersionJobStatus.Succeeded, ErrorMessage = null });

        (await harness.CreateVersionJobStore().GetAsync(pending.JobId)).Should().Be(failed);
    }

    [IntegrationTest]
    public async Task SaveAsync_RunningAndSucceededOnTwoNodesAtOnce_EndsSucceeded()
    {
        await using var harness = await CreateHarnessAsync();
        var firstNode = harness.CreateVersionJobStore();
        var secondNode = harness.CreateVersionJobStore();
        for (var iteration = 0; iteration < 25; iteration++)
        {
            var running = Job() with { Status = VersionJobStatus.Running };
            var succeeded = running with { Status = VersionJobStatus.Succeeded, AppliedChanges = iteration };
            using var start = new Barrier(2);

            await Task.WhenAll(
                Task.Run(() => { start.SignalAndWait(); return firstNode.SaveAsync(running); }),
                Task.Run(() => { start.SignalAndWait(); return secondNode.SaveAsync(succeeded); }));

            (await firstNode.GetAsync(running.JobId)).Should().Be(succeeded);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_HigherStageWithDifferentIdentity_KeepsAuthoritativeJob()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateVersionJobStore();
        var pending = Job();
        var completed = pending with { Status = VersionJobStatus.Succeeded };
        VersionJob[] replacements =
        [
            completed with { Service = "other-service" },
            completed with { VersionId = Guid.NewGuid() },
            completed with { Kind = VersionJobKind.Post },
            completed with { Policy = VersionReconcilePolicy.DefaultWins },
            completed with { CreatedAt = pending.CreatedAt.AddSeconds(1) },
            completed with { ConflictDetection = VersionConflictDetection.ByObject }
        ];
        await store.SaveAsync(pending);

        foreach (var replacement in replacements)
        {
            await harness.CreateVersionJobStore().SaveAsync(replacement);
            (await store.GetAsync(pending.JobId)).Should().Be(pending);
        }

        await store.SaveAsync(completed);
        (await store.GetAsync(pending.JobId)).Should().Be(completed);
    }

    [IntegrationTest]
    public async Task SaveAsync_HigherStageOverUnreadableRecord_KeepsTheRecord()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateVersionJobStore();
        var pending = Job();
        await store.SaveAsync(pending);
        foreach (var payload in new[] { "[1]", "null" })
        {
            await harness.ExecuteAsync(
                $"UPDATE {harness.Table("version_jobs")} SET record = @record::json WHERE job_id = @id",
                new NpgsqlParameter("record", payload), new NpgsqlParameter("id", pending.JobId));

            await harness.CreateVersionJobStore().SaveAsync(pending with { Status = VersionJobStatus.Succeeded });

            (await store.GetAsync(pending.JobId)).Should().BeNull();
            (await harness.ScalarAsync<string>(
                $"SELECT record::text FROM {harness.Table("version_jobs")} WHERE job_id = @id",
                new NpgsqlParameter("id", pending.JobId))).Should().Be(payload);
        }
    }

    [IntegrationTest]
    public async Task GetAsync_UnreadableRecord_ReturnsNullAndKeepsTheRow()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateVersionJobStore();
        var job = Job();
        await store.SaveAsync(job);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("version_jobs")} SET record = '[1]' WHERE job_id = @id",
            new NpgsqlParameter("id", job.JobId));

        (await store.GetAsync(job.JobId)).Should().BeNull();
        (await harness.ScalarAsync<long>(
            $"SELECT count(*) FROM {harness.Table("version_jobs")} WHERE job_id = @id",
            new NpgsqlParameter("id", job.JobId))).Should().Be(1);
    }

    [IntegrationTest]
    public async Task SaveAsync_WhenExpiredRowIsSweptAfterInsert_PersistsTheAdvance()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateVersionJobStore();
        var pending = Job();
        await store.SaveAsync(pending);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("version_jobs")} SET expires_at = now() - interval '1 second'");

        // Force the expiry sweep into the gap after an insert conflicts and before a later update.
        // An atomic upsert renews the row before this statement trigger can sweep it.
        await harness.ExecuteAsync($"""
            CREATE FUNCTION {harness.Table("sweep_version_jobs_after_insert")}() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN
                DELETE FROM {harness.Table("version_jobs")} WHERE expires_at <= now();
                RETURN NULL;
            END;
            $body$;
            CREATE TRIGGER sweep_version_jobs_after_insert AFTER INSERT ON {harness.Table("version_jobs")}
            FOR EACH STATEMENT EXECUTE FUNCTION {harness.Table("sweep_version_jobs_after_insert")}();
            """);

        var succeeded = pending with { Status = VersionJobStatus.Succeeded };
        await store.SaveAsync(succeeded);

        (await harness.CreateVersionJobStore().GetAsync(pending.JobId)).Should().Be(succeeded);
    }

    [IntegrationTest]
    public async Task GetAsync_AfterRetention_ReturnsNullAndTheSweepDeletesTheJob()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateVersionJobStore();
        var expired = Job();
        var kept = Job();
        await store.SaveAsync(expired);
        await store.SaveAsync(kept);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("version_jobs")} SET expires_at = now() - interval '1 second' WHERE job_id = @id",
            new NpgsqlParameter("id", expired.JobId));

        (await store.GetAsync(expired.JobId)).Should().BeNull();
        (await store.DeleteExpiredAsync(batchSize: 100, CancellationToken.None)).Should().Be(1);
        (await store.GetAsync(kept.JobId)).Should().Be(kept);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresVersionJobStoreTests));

    private static VersionJob Job()
        => new(
            JobId: Guid.NewGuid(),
            Service: "parcels",
            VersionId: Guid.NewGuid(),
            Kind: VersionJobKind.Reconcile,
            Status: VersionJobStatus.Pending,
            Policy: VersionReconcilePolicy.VersionWins,
            CreatedAt: new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero),
            ConflictDetection: VersionConflictDetection.ByAttribute);
}
