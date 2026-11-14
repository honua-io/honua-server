// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
[Collection("Database")]
public sealed class OpsHeartbeatTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public Task InitializeAsync() => _postgres.InitializeAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task PublishAsync_ConcurrentReplicasAndRestart_OnlyOneHeartbeatPerLeaseWindow()
    {
        var stack = $"heartbeat-{Guid.NewGuid():N}";
        var publisher = new RecordingPublisher();
        var store = CreateStore();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            new OpsHeartbeatCoordinator(store, publisher).PublishAsync(stack, CancellationToken.None)));
        await new OpsHeartbeatCoordinator(CreateStore(), publisher).PublishAsync(stack, CancellationToken.None);

        publisher.Count.Should().Be(1);
        await ExpireLeaseAsync(stack);
        await new OpsHeartbeatCoordinator(CreateStore(), publisher).PublishAsync(stack, CancellationToken.None);
        publisher.Count.Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task PublishAsync_DifferentStacks_HaveIndependentHeartbeatWindows()
    {
        var publisher = new RecordingPublisher();
        var coordinator = new OpsHeartbeatCoordinator(CreateStore(), publisher);

        await coordinator.PublishAsync($"heartbeat-{Guid.NewGuid():N}", CancellationToken.None);
        await coordinator.PublishAsync($"heartbeat-{Guid.NewGuid():N}", CancellationToken.None);

        publisher.Count.Should().Be(2);
    }

    private PostgresExecutionJobStore CreateStore() => new(
        new JobStoreDatabase(_postgres.DataSource),
        Options.Create(new JobStoreOptions()),
        NullLogger<PostgresExecutionJobStore>.Instance);

    private async Task ExpireLeaseAsync(string stack)
    {
        await using var command = _postgres.DataSource.CreateCommand(
            "UPDATE honua.leases SET expires_at = now() - interval '1 second' WHERE lease_key = @key");
        command.Parameters.AddWithValue("key", $"ops-heartbeat:{stack}");
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private sealed class RecordingPublisher : IOpsHeartbeatPublisher
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public Task PublishAsync(string stackName, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }
}
