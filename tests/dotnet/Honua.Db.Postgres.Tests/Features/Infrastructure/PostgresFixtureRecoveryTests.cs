// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

public sealed class PostgresFixtureRecoveryTests
{
    [IntegrationTest]
    public async Task DisposeAsync_ContainerCleanupThrows_PreservesSurvivorsAndAllowsReinitialization()
    {
        var state = new PostgresFixture.PostgresSharedState();
        var first = new PostgresFixture(boundedResources: true, state);
        var survivor = new PostgresFixture(boundedResources: true, state);
        var replacement = new PostgresFixture(boundedResources: true, state);
        try
        {
            await first.InitializeAsync();
            await survivor.InitializeAsync();
            var cleanup = new FailingCleanup(state.SharedContainer);
            state.SharedContainer = cleanup;
            await first.DisposeAsync();
            await first.DisposeAsync();
            cleanup.DisposeCalls.Should().Be(0);
            await AssertConnectionWorksAsync(survivor);

            var disposeLastReference = () => survivor.DisposeAsync();
            await disposeLastReference.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("container cleanup failed");
            await survivor.DisposeAsync();
            cleanup.DisposeCalls.Should().Be(1);

            await replacement.InitializeAsync();
            await AssertConnectionWorksAsync(replacement);
            state.SharedRefCount.Should().Be(1);
        }
        finally
        {
            await first.DisposeAsync();
            await survivor.DisposeAsync();
            await replacement.DisposeAsync();
        }
    }

    private static async Task AssertConnectionWorksAsync(PostgresFixture fixture)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        (await command.ExecuteScalarAsync()).Should().Be(1);
    }

    private sealed class FailingCleanup(IAsyncDisposable? container) : IAsyncDisposable
    {
        internal int DisposeCalls { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (container is not null)
                await container.DisposeAsync();
            throw new InvalidOperationException("container cleanup failed");
        }
    }
}
