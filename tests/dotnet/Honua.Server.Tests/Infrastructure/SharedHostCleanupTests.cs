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
public sealed class SharedHostCleanupCollection
{
    public const string Name = "Shared host cleanup";
}

[Collection(SharedHostCleanupCollection.Name)]
[Protocol(TestProtocols.TestQuality)]
public sealed class SharedHostCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureInitializedAsync_StartupFails_ReleasesResourcesAndAllowsRetry(bool cleanupFails)
    {
        await using var postgresAnchor = new AsyncDisposalScope(new PostgresFixture());
        await postgresAnchor.Fixture.InitializeAsync();
        var referencesBefore = PostgresReferenceCount();
        var failure = new InvalidOperationException("Controlled startup failure");
        var failedFactory = new FailingFactory(failure, cleanupFails);

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebAppFixtureSharedBootstrapMixin.EnsureInitializedAsync(
                WebAppFixture.SharedAdminPassword, (_, _) => failedFactory));

        Assert.Same(failure, observed);
        Assert.True(failedFactory.Disposed);
        Assert.Equal(referencesBefore, PostgresReferenceCount());
        Assert.Throws<InvalidOperationException>(() => WebAppFixtureSharedBootstrapMixin.Factory);
        Assert.Throws<InvalidOperationException>(() => WebAppFixtureSharedBootstrapMixin.Postgres);

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

    [Fact]
    public async Task DisposeAsync_BeforeInitialization_CanBeRepeated()
    {
        var fixture = new WebAppFixture();
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();
    }

    [Fact]
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

    private sealed class FailingFactory(Exception failure, bool cleanupFails) : WebApplicationFactory<Program>
    {
        public bool Disposed { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => throw failure;

        public override async ValueTask DisposeAsync()
        {
            Disposed = true;
            await base.DisposeAsync();
            if (cleanupFails)
            {
                throw new InvalidOperationException("Controlled cleanup failure");
            }
        }
    }

    private sealed class AsyncDisposalScope(PostgresFixture fixture) : IAsyncDisposable
    {
        public PostgresFixture Fixture { get; } = fixture;
        public async ValueTask DisposeAsync() => await Fixture.DisposeAsync();
    }
}
