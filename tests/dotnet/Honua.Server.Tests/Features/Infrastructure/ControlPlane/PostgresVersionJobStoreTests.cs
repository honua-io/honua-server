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
