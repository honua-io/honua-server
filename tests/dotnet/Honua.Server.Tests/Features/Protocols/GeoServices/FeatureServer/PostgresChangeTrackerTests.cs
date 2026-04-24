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
