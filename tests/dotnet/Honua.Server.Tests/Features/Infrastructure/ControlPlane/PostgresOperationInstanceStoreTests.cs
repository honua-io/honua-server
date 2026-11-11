// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresOperationInstanceStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task TryCreateAsync_ConcurrentCreates_ExactlyOneWins()
    {
        await using var harness = await CreateHarnessAsync();
        var handle = Handle();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(node =>
            harness.CreateInstanceStore().TryCreateAsync(handle with { CorrelationId = "node-" + node })));

        outcomes.Count(created => created).Should().Be(1);
        var stored = await harness.CreateInstanceStore().GetAsync(handle.OperationInstanceId);
        stored!.CorrelationId.Should().Be("node-" + Array.IndexOf(outcomes, true));
        stored.Version.Should().Be(0);
    }

    [IntegrationTest]
    public async Task TrySetAsync_StaleVersion_IsRefused()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateInstanceStore();
        var handle = Handle();
        await store.TryCreateAsync(handle);

        (await store.TrySetAsync(handle with { Status = OperationHandleStatus.Running }, expectedVersion: 0)).Should().BeTrue();
        (await store.TrySetAsync(handle with { Status = OperationHandleStatus.Completed }, expectedVersion: 0)).Should().BeFalse();

        var stored = await store.GetAsync(handle.OperationInstanceId);
        stored!.Status.Should().Be(OperationHandleStatus.Running);
        stored.Version.Should().Be(1);
    }

    [IntegrationTest]
    public async Task SetAsync_AdvancesTheVersionAndRefusesAnInstanceThatWasNeverAccepted()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateInstanceStore();
        var handle = Handle();
        await store.TryCreateAsync(handle);

        await store.SetAsync(handle with { Status = OperationHandleStatus.Running });

        (await store.GetAsync(handle.OperationInstanceId))!.Version.Should().Be(1);
        var neverAccepted = () => store.SetAsync(Handle());
        await neverAccepted.Should().ThrowAsync<InvalidOperationException>();
    }

    [IntegrationTest]
    public async Task ListActiveAsync_OmitsCompletedInstances()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateInstanceStore();
        var running = Handle();
        var completed = Handle();
        await store.TryCreateAsync(running);
        await store.TryCreateAsync(completed);
        await store.TrySetAsync(completed with { Status = OperationHandleStatus.Completed }, expectedVersion: 0);

        (await store.ListActiveAsync()).Select(handle => handle.OperationInstanceId)
            .Should().Equal(running.OperationInstanceId);
    }

    [IntegrationTest]
    public async Task DeleteExpiredAsync_KeepsActiveInstancesPastRetention()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateInstanceStore();
        var running = Handle();
        var completed = Handle();
        await store.TryCreateAsync(running);
        await store.TryCreateAsync(completed);
        await store.TrySetAsync(completed with { Status = OperationHandleStatus.Completed }, expectedVersion: 0);
        await harness.ExecuteAsync($"UPDATE {harness.Table("operation_instances")} SET expires_at = now() - interval '1 second'");

        (await store.DeleteExpiredAsync(batchSize: 100, CancellationToken.None)).Should().Be(1);

        (await store.GetAsync(running.OperationInstanceId)).Should().NotBeNull();
        (await store.GetAsync(completed.OperationInstanceId)).Should().BeNull();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresOperationInstanceStoreTests));

    private static OperationHandle Handle()
    {
        var now = DateTimeOffset.UtcNow;
        return new OperationHandle
        {
            OperationInstanceId = $"opi-{Guid.NewGuid():N}",
            OperationId = "admin.test.operation",
            Status = OperationHandleStatus.Accepted,
            CorrelationId = "correlation",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
