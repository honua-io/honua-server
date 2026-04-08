// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.DuckDB.Features.FeatureStore;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Verifies that the read-only change tracker reports no changes.
/// </summary>
public class ReadOnlyChangeTrackerTests
{
    private readonly ReadOnlyChangeTracker _tracker = new();

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
