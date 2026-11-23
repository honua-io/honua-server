// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

public sealed class PostgresFixtureResetTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetAsync_ContainerCleanupCompletesOrThrows_UnpublishesDisposedResources(bool failCleanup)
    {
        var pool = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var container = new CleanupResource(failCleanup);
        var state = new PostgresFixture.PostgresSharedState
        {
            SharedDataSource = pool,
            SharedContainer = container,
            SharedConnectionString = pool.ConnectionString,
            SharedInitialized = true,
            BoundedResources = true,
        };

        var reset = () => state.ResetAsync();
        if (failCleanup)
            await reset.Should().ThrowAsync<InvalidOperationException>().WithMessage("cleanup failed");
        else
            await reset.Should().NotThrowAsync();

        state.SharedInitialized.Should().BeFalse();
        state.SharedDataSource.Should().BeNull();
        state.SharedContainer.Should().BeNull();
        state.SharedConnectionString.Should().BeNull();
        state.BoundedResources.Should().BeFalse();
        var openDisposedPool = async () => await pool.OpenConnectionAsync();
        await openDisposedPool.Should().ThrowAsync<ObjectDisposedException>();
        await state.ResetAsync();
        container.DisposeCalls.Should().Be(1);
    }

    [UnitTest]
    public async Task DisposeAsync_WithIdleReleaseDelay_KeepsPoolAliveUntilGraceOrFlush()
    {
        var pool = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var container = new CleanupResource(failCleanup: false);
        var state = new PostgresFixture.PostgresSharedState
        {
            SharedDataSource = pool,
            SharedContainer = container,
            SharedConnectionString = pool.ConnectionString,
            SharedInitialized = true,
            IdleReleaseDelay = TimeSpan.FromSeconds(30),
        };
        var first = new PostgresFixture(boundedResources: false, state);
        var second = new PostgresFixture(boundedResources: false, state);

        await first.InitializeAsync();
        await first.DisposeAsync();

        state.SharedInitialized.Should().BeTrue();
        state.SharedRefCount.Should().Be(0);
        container.DisposeCalls.Should().Be(0);

        await second.InitializeAsync();
        second.DataSource.Should().BeSameAs(pool);
        state.SharedRefCount.Should().Be(1);
        await PostgresFixture.ReleaseIdleNowAsync(state);
        state.SharedInitialized.Should().BeTrue();

        await second.DisposeAsync();
        await PostgresFixture.ReleaseIdleNowAsync(state);
        state.SharedInitialized.Should().BeFalse();
        container.DisposeCalls.Should().Be(1);
    }

    [UnitTest]
    public async Task DisposeAsync_WithShortIdleReleaseDelay_DisposesAfterGrace()
    {
        var pool = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var container = new CleanupResource(failCleanup: false);
        var state = new PostgresFixture.PostgresSharedState
        {
            SharedDataSource = pool,
            SharedContainer = container,
            SharedConnectionString = pool.ConnectionString,
            SharedInitialized = true,
            IdleReleaseDelay = TimeSpan.FromMilliseconds(50),
        };
        var fixture = new PostgresFixture(boundedResources: false, state);

        await fixture.InitializeAsync();
        await fixture.DisposeAsync();

        await container.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        state.SharedInitialized.Should().BeFalse();
        container.DisposeCalls.Should().Be(1);
    }

    private sealed class CleanupResource(bool failCleanup) : IAsyncDisposable
    {
        internal int DisposeCalls { get; private set; }
        internal TaskCompletionSource DisposalCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (!failCleanup)
                DisposalCompleted.TrySetResult();
            return failCleanup
                ? ValueTask.FromException(new InvalidOperationException("cleanup failed"))
                : ValueTask.CompletedTask;
        }
    }
}
