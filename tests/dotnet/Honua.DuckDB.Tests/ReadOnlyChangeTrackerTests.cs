// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.ReadOnlyProviders;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Verifies that the shared no-op change tracker reports no changes
/// (as registered by the DuckDB provider).
/// </summary>
public class ReadOnlyChangeTrackerTests
{
    private readonly NoOpChangeTracker _tracker = new();

    [Fact]
    public async Task GetCurrentGenerationAsync_ReturnsZero()
    {
        var result = await _tracker.GetCurrentGenerationAsync();
        Assert.Equal(0L, result);
    }

    [Fact]
    public async Task GetChangesSinceAsync_ReturnsEmpty()
    {
        var result = await _tracker.GetChangesSinceAsync(0, [1, 2]);
        Assert.Empty(result);
    }
}
