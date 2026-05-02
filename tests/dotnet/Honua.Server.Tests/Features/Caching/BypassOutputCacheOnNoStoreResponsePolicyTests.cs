// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Unit tests for <see cref="BypassOutputCacheOnNoStoreResponsePolicy"/>.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class BypassOutputCacheOnNoStoreResponsePolicyTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_NoStore_DisablesCacheStorage()
    {
        var policy = new BypassOutputCacheOnNoStoreResponsePolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";

        var context = new OutputCacheContext { HttpContext = httpContext, AllowCacheStorage = true };

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_NoStoreCaseInsensitive_DisablesCacheStorage()
    {
        var policy = new BypassOutputCacheOnNoStoreResponsePolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Headers[HeaderNames.CacheControl] = "No-Store";

        var context = new OutputCacheContext { HttpContext = httpContext, AllowCacheStorage = true };

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_PublicMaxAge_LeavesCacheStorageEnabled()
    {
        var policy = new BypassOutputCacheOnNoStoreResponsePolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Headers[HeaderNames.CacheControl] = "public, max-age=3600";

        var context = new OutputCacheContext { HttpContext = httpContext, AllowCacheStorage = true };

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_NoCacheControlHeader_LeavesCacheStorageEnabled()
    {
        var policy = new BypassOutputCacheOnNoStoreResponsePolicy();
        var httpContext = new DefaultHttpContext();

        var context = new OutputCacheContext { HttpContext = httpContext, AllowCacheStorage = true };

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeTrue();
    }
}
