// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Infrastructure;

public sealed partial class SharedHostCleanupTests
{
    [IntegrationTest]
    public async Task EnsureInitializedAsync_ConcurrentColdStart_HoldsBootstrapLockUntilHostStarts()
    {
        await using var anchor = new AsyncDisposalScope(new PostgresFixture());
        await anchor.Fixture.InitializeAsync();
        var referencesBefore = PostgresReferenceCount();
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishStartup = new ManualResetEventSlim();
        var acquired = 0;
        var factoriesBuilt = 0;
        Task[] waiters = [];
        var owner = Task.Run(async () =>
        {
            await WebAppFixtureSharedBootstrapMixin.EnsureInitializedAsync(WebAppFixture.SharedAdminPassword,
                (connection, password) =>
                {
                    Interlocked.Increment(ref factoriesBuilt);
                    return WebAppFixtureSharedBootstrapMixin.BuildSharedFactory(connection, password)
                        .WithWebHostBuilder(builder => builder.ConfigureServices(_ =>
                        {
                            startupEntered.TrySetResult();
                            if (!finishStartup.Wait(TimeSpan.FromSeconds(30)))
                            {
                                throw new TimeoutException("Controlled startup was not released.");
                            }
                        }));
                });
            Interlocked.Increment(ref acquired);
            return WebAppFixtureSharedBootstrapMixin.Factory.Services;
        });
        try
        {
            await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            waiters = Enumerable.Range(0, 3).Select(_ =>
                WebAppFixtureSharedBootstrapMixin.EnsureInitializedAsync(WebAppFixture.SharedAdminPassword)).ToArray();
            Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
            finishStartup.Set();
            var provider = await owner.WaitAsync(TimeSpan.FromSeconds(30));
            var providers = await Task.WhenAll(waiters.Select(async waiter =>
            {
                await waiter;
                return WebAppFixtureSharedBootstrapMixin.Factory.Services;
            }));
            Assert.All(providers, candidate => Assert.Same(provider, candidate));
            Assert.Equal(1, factoriesBuilt);
            Assert.Equal(referencesBefore + 1, PostgresReferenceCount());
        }
        finally
        {
            finishStartup.Set();
            try
            {
                await owner.WaitAsync(TimeSpan.FromSeconds(30));
                await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                acquired += waiters.Count(waiter => waiter.IsCompletedSuccessfully);
                for (var index = 0; index < acquired; index++)
                {
                    await WebAppFixtureSharedBootstrapMixin.ReleaseAsync();
                }
            }
        }
        Assert.Equal(referencesBefore, PostgresReferenceCount());
    }
}
