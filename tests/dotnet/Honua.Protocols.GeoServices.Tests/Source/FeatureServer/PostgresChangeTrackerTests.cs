// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
public sealed class PostgresChangeTrackerTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetCurrentGeneration_ReturnsNonNegativeValue()
    {
        var tracker = _fixture.GetService<IChangeTracker>();
        var gen = await tracker.GetCurrentGenerationAsync();

        gen.Should().BeGreaterThanOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_EmptyLayerIds_ReturnsEmpty()
    {
        var tracker = _fixture.GetService<IChangeTracker>();
        var changes = await tracker.GetChangesSinceAsync(0, []);

        changes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_NoChanges_ReturnsEmpty()
    {
        var tracker = _fixture.GetService<IChangeTracker>();
        var gen = await tracker.GetCurrentGenerationAsync();

        // No changes should exist after the current generation
        var changes = await tracker.GetChangesSinceAsync(gen, [0]);
        changes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_LayerIdFiltering_OnlyReturnsRequestedLayers()
    {
        var tracker = _fixture.GetService<IChangeTracker>();

        // Get changes for a non-existent layer — should be empty
        var changes = await tracker.GetChangesSinceAsync(0, [99999]);
        changes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_ObjectIdFilter_RestrictsFeedSqlSide()
    {
        var tracker = _fixture.GetService<IChangeTracker>();

        // An id that can never match yields an empty feed (and exercises the SQL-side binding).
        var nonMatching = await tracker.GetChangesSinceAsync(0, [0], new HashSet<long> { long.MaxValue });
        nonMatching.Should().BeEmpty();

        // An empty id set short-circuits to an empty feed.
        var emptyFilter = await tracker.GetChangesSinceAsync(0, [0], new HashSet<long>());
        emptyFilter.Should().BeEmpty();

        // A null filter matches the unfiltered overload; when changes exist, filtering to one of
        // their ids returns exactly that feature's collapsed changes.
        var unfiltered = await tracker.GetChangesSinceAsync(0, [0], objectIds: null);
        var baseline = await tracker.GetChangesSinceAsync(0, [0]);
        unfiltered.Should().BeEquivalentTo(baseline);

        if (unfiltered.Count > 0)
        {
            var targetId = unfiltered[0].ObjectId;
            var filtered = await tracker.GetChangesSinceAsync(0, [0], new HashSet<long> { targetId });
            filtered.Should().NotBeEmpty();
            filtered.Should().OnlyContain(change => change.ObjectId == targetId);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task ChangeCollapsing_OperationValues_AreValidEnumMembers()
    {
        var tracker = _fixture.GetService<IChangeTracker>();
        var changes = await tracker.GetChangesSinceAsync(0, [0]);

        foreach (var change in changes)
        {
            change.Operation.Should().BeOneOf(
                FeatureChangeOperation.Insert,
                FeatureChangeOperation.Update,
                FeatureChangeOperation.Delete);
        }
    }
}
