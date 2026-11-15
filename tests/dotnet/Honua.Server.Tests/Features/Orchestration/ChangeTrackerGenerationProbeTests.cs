// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Orchestration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Orchestration;

public sealed class ChangeTrackerGenerationProbeTests
{
    [UnitTest]
    public async Task GetLatestGenerationAsync_WhenLaterChangesArrive_DefersThemToNextWindow()
    {
        var tracker = new WindowedChangeTracker { CurrentGeneration = 3 };
        using var services = new ServiceCollection().AddScoped<IChangeTracker>(_ => tracker).BuildServiceProvider();
        var probe = new ChangeTrackerGenerationProbe(services.GetRequiredService<IServiceScopeFactory>());

        (await probe.GetLatestGenerationAsync(1, [10])).Should().Be(2);
        tracker.CurrentGeneration = 4;
        (await probe.GetLatestGenerationAsync(3, [10])).Should().Be(4);
    }

    [UnitTest]
    public async Task GetLatestGenerationAsync_WhenCursorHasCaughtUp_ReturnsNoChange()
    {
        var tracker = new WindowedChangeTracker { CurrentGeneration = 3 };
        using var services = new ServiceCollection().AddScoped<IChangeTracker>(_ => tracker).BuildServiceProvider();
        var probe = new ChangeTrackerGenerationProbe(services.GetRequiredService<IServiceScopeFactory>());

        (await probe.GetLatestGenerationAsync(3, [10])).Should().BeNull();
        tracker.WindowReads.Should().Be(0);
    }

    [UnitTest]
    public async Task GetLatestGenerationAsync_WhenTrackerIsUnavailable_ReturnsNoChange()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var probe = new ChangeTrackerGenerationProbe(services.GetRequiredService<IServiceScopeFactory>());

        (await probe.GetLatestGenerationAsync(0, [10])).Should().BeNull();
    }

    [UnitTest]
    public async Task GetLatestGenerationAsync_WhenNoLayersAreWatched_ReturnsNoChange()
    {
        var tracker = new WindowedChangeTracker { CurrentGeneration = 3 };
        using var services = new ServiceCollection().AddScoped<IChangeTracker>(_ => tracker).BuildServiceProvider();
        var probe = new ChangeTrackerGenerationProbe(services.GetRequiredService<IServiceScopeFactory>());

        (await probe.GetLatestGenerationAsync(0, [])).Should().BeNull();
        tracker.WindowReads.Should().Be(0);
    }

    private sealed class WindowedChangeTracker : IChangeTracker
    {
        public long CurrentGeneration { get; set; }
        public int WindowReads { get; private set; }

        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentGeneration);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration, int[] layerIds, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("An unbounded read can consume changes beyond the captured generation.");

        public Task<IReadOnlyList<FeatureChange>> GetChangesInWindowAsync(
            long sinceGeneration,
            long throughGeneration,
            int[] layerIds,
            string? excludeOriginReplicaId,
            CancellationToken cancellationToken = default)
        {
            WindowReads++;
            layerIds.Should().Equal(10);
            excludeOriginReplicaId.Should().BeNull();
            IReadOnlyList<FeatureChange> changes = new long[] { 2, 4 }
                .Where(generation => generation > sinceGeneration && generation <= throughGeneration)
                .Select(generation => new FeatureChange
                {
                    ChangeId = generation, Generation = generation, LayerId = 10, ObjectId = generation,
                    Operation = FeatureChangeOperation.Delete, ChangedAt = DateTimeOffset.UtcNow
                }).ToArray();
            return Task.FromResult(changes);
        }
    }
}
