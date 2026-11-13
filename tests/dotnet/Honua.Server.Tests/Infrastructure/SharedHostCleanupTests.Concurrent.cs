// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Infrastructure;

public sealed partial class SharedHostCleanupTests
{
    [IntegrationTest]
    public async Task EnsureInitializedAsync_ConcurrentColdStart_HoldsBootstrapLockUntilHostStarts()
    {
        await using var anchor = new AsyncDisposalScope(new PostgresFixture());
        await anchor.Fixture.InitializeAsync();
        var referencesBefore = PostgresReferenceCount();
        using var startup = new ControlledStartup();
        Task[] waiters = [];
        var owner = Task.Run(startup.AcquireOwnerAsync);
        try
        {
            await startup.WaitUntilEnteredAsync();
            waiters = Enumerable.Range(0, 3).Select(_ =>
                startup.AcquireWaiterAsync()).ToArray();
            Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
            await startup.AssertSharedProvidersAsync(owner, waiters);
            Assert.Equal(referencesBefore + 1, PostgresReferenceCount());
        }
        finally
        {
            await startup.ReleaseReferencesAsync(owner, waiters);
        }
        Assert.Equal(referencesBefore, PostgresReferenceCount());
    }

    private sealed class ControlledStartup : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _finish = new();
        private int _acquired;
        private int _factoriesBuilt;

        public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(Timeout);

        public async Task<IServiceProvider> AcquireOwnerAsync()
        {
            await AcquireWaiterAsync();
            Interlocked.Increment(ref _acquired);
            return WebAppFixtureSharedBootstrapMixin.Factory.Services;
        }

        public Task AcquireWaiterAsync() => WebAppFixtureSharedBootstrapMixin.EnsureInitializedAsync(
            WebAppFixture.SharedAdminPassword, BuildGatedFactory);

        public async Task AssertSharedProvidersAsync(Task<IServiceProvider> owner, Task[] waiters)
        {
            _finish.Set();
            var provider = await owner.WaitAsync(Timeout);
            var providers = await Task.WhenAll(waiters.Select(async waiter =>
            {
                await waiter;
                return WebAppFixtureSharedBootstrapMixin.Factory.Services;
            }));
            Assert.All(providers, candidate => Assert.Same(provider, candidate));
            Assert.Equal(1, _factoriesBuilt);
        }

        public async Task ReleaseReferencesAsync(Task<IServiceProvider> owner, Task[] waiters)
        {
            _finish.Set();
            try
            {
                await owner.WaitAsync(Timeout);
                await Task.WhenAll(waiters).WaitAsync(Timeout);
            }
            finally
            {
                _acquired += waiters.Count(waiter => waiter.IsCompletedSuccessfully);
                for (var index = 0; index < _acquired; index++)
                {
                    await WebAppFixtureSharedBootstrapMixin.ReleaseAsync();
                }
            }
        }

        public void Dispose() => _finish.Dispose();

        private WebApplicationFactory<Program> BuildGatedFactory(string connection, string password)
        {
            Interlocked.Increment(ref _factoriesBuilt);
            return WebAppFixtureSharedBootstrapMixin.BuildSharedFactory(connection, password)
                .WithWebHostBuilder(builder => builder.ConfigureServices(_ => WaitForRelease()));
        }

        private void WaitForRelease()
        {
            _entered.TrySetResult();
            if (!_finish.Wait(Timeout))
            {
                throw new TimeoutException("Controlled startup was not released.");
            }
        }
    }
}
