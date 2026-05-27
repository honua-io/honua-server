// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Verifies that the shared read-only feature writer rejects all write operations
/// (as registered by the DuckDB provider).
/// </summary>
public class ReadOnlyFeatureWriterTests
{
    private readonly ReadOnlyFeatureWriter _writer = new("DuckDB");

    [Fact]
    public async Task CreateAsync_ThrowsNotSupported()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _writer.CreateAsync(0, feature));
    }

    [Fact]
    public async Task UpdateAsync_ThrowsNotSupported()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _writer.UpdateAsync(0, feature));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsNotSupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _writer.DeleteAsync(0, 1));
    }

    [Fact]
    public async Task ApplyEditsAsync_ThrowsNotSupported()
    {
        var batch = FeatureEditBatch.Create([], [], []);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _writer.ApplyEditsAsync(0, batch));
    }
}
