// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Metadata.Caching;

public sealed class MetadataV2GenerationCacheTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ForGeneration_AfterConfigurationMutation_LoadsNewSnapshotWithinTtl()
    {
        var cache = CreateCache();
        var initial = cache.ForGeneration("production", 1);
        await initial.GetOrLoadAsync("production", _ => ValueTask.FromResult(Snapshot(1)));
        var current = cache.ForGeneration("production", 2);

        var snapshot = await current.GetOrLoadAsync("production", _ => ValueTask.FromResult(Snapshot(2)));

        snapshot.Revision.Should().Be(2);
        current.Should().NotBeSameAs(initial);
        cache.ForGeneration("production", 1).Should().BeSameAs(current);
        cache.ForGeneration("staging", 1).Should().NotBeSameAs(current);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task Invalidate_AfterPartitionCreated_DropsCurrentGenerationSnapshot()
    {
        var cache = CreateCache();
        var partition = cache.ForGeneration("production", 1);
        await partition.GetOrLoadAsync("production", _ => ValueTask.FromResult(Snapshot(1)));

        cache.Invalidate("production");

        var snapshot = await partition.GetOrLoadAsync("production", _ => ValueTask.FromResult(Snapshot(2)));
        snapshot.Revision.Should().Be(2);
    }

    private static MetadataV2GraphSnapshotCache CreateCache() => new(Options.Create(new CacheOptions
    {
        MetadataGraphCacheEnabled = true,
        MetadataGraphTtlSeconds = 60
    }));

    private static MetadataV2GraphSnapshot Snapshot(long revision) => new(new MetadataV2Graph
    {
        Environment = "production",
        Revision = revision,
        GeneratedAt = DateTimeOffset.UtcNow
    }, $"\"rev-{revision}\"", DateTimeOffset.UtcNow);
}
