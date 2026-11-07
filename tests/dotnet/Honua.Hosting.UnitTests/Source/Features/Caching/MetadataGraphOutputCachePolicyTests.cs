// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

public sealed class MetadataGraphOutputCachePolicyTests
{
    [UnitTest]
    public async Task CacheRequestAsync_GraphEtagChanges_VariesCachedResponse()
    {
        var provider = Substitute.For<IMetadataV2GraphProvider>();
        using var services = new ServiceCollection().AddSingleton(provider).BuildServiceProvider();
        var graph = new MetadataV2Graph { Revision = 1 };
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(graph, "before", DateTimeOffset.UtcNow));
        var policy = new MetadataGraphOutputCachePolicy();
        var before = new OutputCacheContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
        await policy.CacheRequestAsync(before, CancellationToken.None);
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(graph with { Revision = 2 }, "after", DateTimeOffset.UtcNow));
        var after = new OutputCacheContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
        await policy.CacheRequestAsync(after, CancellationToken.None);
        Assert.Equal("before", before.CacheVaryByRules.VaryByValues["metadata-graph"]);
        Assert.Equal("after", after.CacheVaryByRules.VaryByValues["metadata-graph"]);
    }
}
