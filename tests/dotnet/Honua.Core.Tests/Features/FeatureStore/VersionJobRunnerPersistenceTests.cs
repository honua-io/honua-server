// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class VersionJobRunnerPersistenceTests
{
    [UnitTest]
    public async Task StartPostAsync_CompletionSaveFails_RetriesSuccessWithoutReplayingPost()
    {
        var manager = Substitute.For<IVersionManager>();
        manager.PostAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new VersionPostResult(true, 3, 1, false));
        var invalidator = Substitute.For<IQueryResponseCacheInvalidator>();
        var completion = new TaskCompletionSource<VersionJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = Substitute.For<IVersionJobStore>();
        var terminalAttempts = 0;
        store.SaveAsync(Arg.Any<VersionJob>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var job = call.Arg<VersionJob>();
            if (job.Status is VersionJobStatus.Pending or VersionJobStatus.Running)
            {
                return Task.CompletedTask;
            }

            if (Interlocked.Increment(ref terminalAttempts) == 1)
            {
                return Task.FromException(new InvalidOperationException("Store unavailable"));
            }

            completion.TrySetResult(job);
            return Task.CompletedTask;
        });
        var services = new ServiceCollection();
        services.AddSingleton(manager);
        services.AddSingleton(store);
        services.AddSingleton(invalidator);
        await using var provider = services.BuildServiceProvider();
        var runner = new VersionJobRunner(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<VersionJobRunner>.Instance);

        var started = await runner.StartPostAsync("svc", Guid.NewGuid());
        var completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        completed.JobId.Should().Be(started.JobId);
        completed.Status.Should().Be(VersionJobStatus.Succeeded);
        completed.AppliedChanges.Should().Be(3);
        await manager.Received(1).PostAsync(started.VersionId, Arg.Any<CancellationToken>());
        await invalidator.Received(1).InvalidateQueryResponsesAsync(Arg.Any<CancellationToken>());
        await store.DidNotReceive().SaveAsync(
            Arg.Is<VersionJob>(job => job.Status == VersionJobStatus.Failed), Arg.Any<CancellationToken>());
    }
}
