// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

/// <summary>
/// Decides when the Metadata v2 read path revalidates its cached snapshot per request
/// (honua-server#6762): automatically on Lambda without the Redis broadcast, never on other
/// hosts unless configured, and always as configured when the option is set.
/// </summary>
public sealed class MetadataGraphRevalidationCompositionTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(true, false, null, true)]
    [InlineData(false, false, null, false)]
    [InlineData(true, true, null, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, false, false)]
    public void ShouldRevalidateMetadataGraphOnRead_FollowsHostAndOption(
        bool isLambda,
        bool redisBroadcastComposed,
        bool? configured,
        bool expected)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<CacheOptions>>(
            Options.Create(new CacheOptions { MetadataGraphRevalidateOnRead = configured }));
        services.AddMetadataV2GraphSnapshotCache();
        if (redisBroadcastComposed)
        {
            services.AddSingleton<IMetadataV2GraphCacheInvalidator, BroadcastInvalidator>();
        }

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<MetadataV2GraphSnapshotCache>();

        Honua.Db.Postgres.ServiceCollectionExtensions
            .ShouldRevalidateMetadataGraphOnRead(provider, cache, isLambda)
            .Should().Be(expected);
    }

    private sealed class BroadcastInvalidator : IMetadataV2GraphCacheInvalidator
    {
        public void Invalidate(string environment)
        {
        }

        public void InvalidateAll()
        {
        }
    }
}
