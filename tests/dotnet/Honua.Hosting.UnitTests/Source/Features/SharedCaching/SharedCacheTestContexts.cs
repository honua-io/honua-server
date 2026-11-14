// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

internal static class SharedCacheTestContexts
{
    internal static DefaultHttpContext GrantedQuery()
    {
        var context = AnonymousGet(ResponseCacheClass.FeatureQuery);
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        return context;
    }

    internal static DefaultHttpContext GrantedTile(SharedCacheOptions? options = null)
    {
        var context = AnonymousGet(ResponseCacheClass.Tile, options);
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        return context;
    }

    internal static DefaultHttpContext AnonymousGet(
        ResponseCacheClass cacheClass, SharedCacheOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddOptions<SharedCacheOptions>().Configure(configured =>
        {
            configured.Emergency = (options ?? configured).Emergency;
            configured.Layers = (options ?? configured).Layers;
            configured.CacheTagHeader = (options ?? configured).CacheTagHeader;
        });
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Get;
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new SharedCacheClassMetadata(cacheClass)), "declared"));
        return context;
    }
}
