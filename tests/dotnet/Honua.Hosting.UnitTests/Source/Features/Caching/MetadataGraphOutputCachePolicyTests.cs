// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Cache)]
public sealed class MetadataGraphOutputCachePolicyTests
{
    [UnitTest]
    public async Task CacheRequestAsync_GraphEtagChanges_VariesCachedResponse()
    {
        var graph = new MetadataV2Graph { Revision = 1 };
        var provider = new TestMetadataV2GraphProvider(graph, "before");
        using var services = new ServiceCollection().AddSingleton<IMetadataV2GraphProvider>(provider).BuildServiceProvider();
        var policy = new MetadataGraphOutputCachePolicy();
        var before = new OutputCacheContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
        await policy.CacheRequestAsync(before, CancellationToken.None);
        provider.SetGraph(graph with { Revision = 2 }, "after");
        var after = new OutputCacheContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
        await policy.CacheRequestAsync(after, CancellationToken.None);
        Assert.Equal("before", before.CacheVaryByRules.VaryByValues["metadata-graph"]);
        Assert.Equal("after", after.CacheVaryByRules.VaryByValues["metadata-graph"]);
    }
}
