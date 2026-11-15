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

    private sealed class CleanupResource(bool failCleanup) : IAsyncDisposable
    {
        internal int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return failCleanup
                ? ValueTask.FromException(new InvalidOperationException("cleanup failed"))
                : ValueTask.CompletedTask;
        }
    }
}
