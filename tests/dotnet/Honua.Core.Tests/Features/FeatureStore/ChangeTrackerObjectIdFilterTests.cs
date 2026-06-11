// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore;

/// <summary>
/// Unit tests for the <see cref="IChangeTracker"/> object-id-filtered overload's default
/// (client-side) implementation, used by providers that cannot push the filter into the store.
/// </summary>
public sealed class ChangeTrackerObjectIdFilterTests
{
    [UnitTest]
    public async Task GetChangesSince_WithObjectIdFilter_DefaultImplementationFiltersClientSide()
    {
        IChangeTracker tracker = new StubChangeTracker(
            CreateChange(objectId: 1, FeatureChangeOperation.Update),
            CreateChange(objectId: 2, FeatureChangeOperation.Delete),
            CreateChange(objectId: 3, FeatureChangeOperation.Insert));

        var filtered = await tracker.GetChangesSinceAsync(0, [0], new HashSet<long> { 1, 3 });

        filtered.Should().HaveCount(2);
        filtered.Select(change => change.ObjectId).Should().Equal(1, 3);
    }

    [UnitTest]
    public async Task GetChangesSince_WithNullObjectIdFilter_ReturnsUnfilteredFeed()
    {
        IChangeTracker tracker = new StubChangeTracker(
            CreateChange(objectId: 1, FeatureChangeOperation.Update),
            CreateChange(objectId: 2, FeatureChangeOperation.Delete));

        var changes = await tracker.GetChangesSinceAsync(0, [0], objectIds: null);

        changes.Should().HaveCount(2);
    }

    [UnitTest]
    public async Task GetChangesSince_WithEmptyObjectIdFilter_ReturnsEmptyWithoutQueryingFeed()
    {
        var stub = new StubChangeTracker(CreateChange(objectId: 1, FeatureChangeOperation.Update));
        IChangeTracker tracker = stub;

        var changes = await tracker.GetChangesSinceAsync(0, [0], new HashSet<long>());

        changes.Should().BeEmpty();
        stub.UnfilteredCalls.Should().Be(0, "an empty id set can never match, so the feed must not be materialized");
    }

    private static FeatureChange CreateChange(long objectId, FeatureChangeOperation operation) => new()
    {
        ChangeId = objectId,
        Generation = objectId,
        LayerId = 0,
        ObjectId = objectId,
        Operation = operation,
        ChangedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Implements only the abstract <see cref="IChangeTracker"/> members so the interface's default
    /// client-side filter is exercised.
    /// </summary>
    private sealed class StubChangeTracker : IChangeTracker
    {
        private readonly FeatureChange[] _changes;

        public StubChangeTracker(params FeatureChange[] changes) => _changes = changes;

        public int UnfilteredCalls { get; private set; }

        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((long)_changes.Length);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            CancellationToken cancellationToken = default)
        {
            UnfilteredCalls++;
            return Task.FromResult<IReadOnlyList<FeatureChange>>(_changes);
        }
    }
}
