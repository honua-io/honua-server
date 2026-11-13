// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedHostCleanupGroup
{
    public const string Name = "Shared host cleanup";
}

[Collection(SharedHostCleanupGroup.Name)]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed partial class SharedHostCleanupTests
{
    [IntegrationTest]
    public Task EnsureInitializedAsync_StartupFails_ReleasesResourcesAndAllowsRetry() =>
        AssertFailedStartupReleasesResourcesAsync(new StartupFailureFactory());

    [IntegrationTest]
    public Task EnsureInitializedAsync_StartupAndDisposalFail_ReleasesResourcesAndAllowsRetry() =>
        AssertFailedStartupReleasesResourcesAsync(new StartupAndDisposalFailureFactory());

    private static async Task AssertFailedStartupReleasesResourcesAsync(StartupFailureFactory failedFactory)
    {
        await using var postgresAnchor = new AsyncDisposalScope(new PostgresFixture());
        await postgresAnchor.Fixture.InitializeAsync();
        var referencesBefore = PostgresReferenceCount();

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebAppFixtureSharedBootstrapMixin.EnsureInitializedAsync(
                WebAppFixture.SharedAdminPassword, (_, _) => failedFactory));

        Assert.Same(failedFactory.Failure, observed);
        Assert.True(failedFactory.Disposed);
        Assert.Equal(referencesBefore, PostgresReferenceCount());
        Assert.Throws<InvalidOperationException>(() => WebAppFixtureSharedBootstrapMixin.Factory);
        Assert.Throws<InvalidOperationException>(() => WebAppFixtureSharedBootstrapMixin.Postgres);

        await AssertRetryUsesSharedProviderAsync(referencesBefore);
    }

    private static async Task AssertRetryUsesSharedProviderAsync(int referencesBefore)
    {
        var acquired = 0;
        try
        {
            await WebAppFixtureSharedBootstrapMixin.EnsureInitializedAsync(WebAppFixture.SharedAdminPassword);
            acquired++;
            var provider = WebAppFixtureSharedBootstrapMixin.Factory.Services;
            await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            {
                await WebAppFixtureSharedBootstrapMixin.EnsureInitializedAsync(WebAppFixture.SharedAdminPassword);
                Interlocked.Increment(ref acquired);
                Assert.Same(provider, WebAppFixtureSharedBootstrapMixin.Factory.Services);
            }));
            Assert.Equal(referencesBefore + 1, PostgresReferenceCount());
        }
        finally
        {
            for (var index = 0; index < acquired; index++)
            {
                await WebAppFixtureSharedBootstrapMixin.ReleaseAsync();
            }
        }
        Assert.Equal(referencesBefore, PostgresReferenceCount());
    }

    [IntegrationTest]
    public async Task DisposeAsync_BeforeInitialization_CanBeRepeated()
    {
        var fixture = new WebAppFixture();
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();
    }

    [IntegrationTest]
    public async Task DisposeAsync_AfterSharedInitialization_ReleasesOnlyItsOwnReference()
    {
        var first = new WebAppFixture();
        var second = new WebAppFixture();
        try
        {
            await first.InitializeAsync();
            await second.InitializeAsync();
            await first.DisposeAsync();
            await first.DisposeAsync();
            using var response = await second.Client.GetAsync("/healthz/live");
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private static int PostgresReferenceCount()
    {
        var state = typeof(PostgresFixture).GetField("SharedState", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        return (int)state.GetType().GetProperty("SharedRefCount")!.GetValue(state)!;
    }

    private class StartupFailureFactory : WebApplicationFactory<Program>
    {
        public InvalidOperationException Failure { get; } = new("Controlled startup failure");
        public bool Disposed { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => throw Failure;

        public override async ValueTask DisposeAsync()
        {
            Disposed = true;
            await base.DisposeAsync();
        }
    }

    private sealed class StartupAndDisposalFailureFactory : StartupFailureFactory
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            throw new InvalidOperationException("Controlled cleanup failure");
        }
    }

    private sealed class AsyncDisposalScope(PostgresFixture fixture) : IAsyncDisposable
    {
        public PostgresFixture Fixture { get; } = fixture;
        public async ValueTask DisposeAsync() => await Fixture.DisposeAsync();
    }
}
