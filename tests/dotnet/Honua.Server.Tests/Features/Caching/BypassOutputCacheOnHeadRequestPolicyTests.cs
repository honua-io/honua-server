// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Unit tests for <see cref="BypassOutputCacheOnHeadRequestPolicy"/>.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class BypassOutputCacheOnHeadRequestPolicyTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CacheRequestAsync_Head_DisablesLookupAndStorage()
    {
        var policy = new BypassOutputCacheOnHeadRequestPolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Head;

        var context = new OutputCacheContext
        {
            HttpContext = httpContext,
            EnableOutputCaching = true,
            AllowCacheLookup = true,
            AllowCacheStorage = true,
        };

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeFalse();
        context.AllowCacheLookup.Should().BeFalse();
        context.AllowCacheStorage.Should().BeFalse(
            "a cached HEAD entry has an empty body and replays with Content-Length: 0");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_Head_DisablesStorage()
    {
        var policy = new BypassOutputCacheOnHeadRequestPolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Head;

        var context = new OutputCacheContext { HttpContext = httpContext, AllowCacheStorage = true };

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CacheRequestAsync_Get_LeavesCachingEnabled()
    {
        var policy = new BypassOutputCacheOnHeadRequestPolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;

        var context = new OutputCacheContext
        {
            HttpContext = httpContext,
            EnableOutputCaching = true,
            AllowCacheLookup = true,
            AllowCacheStorage = true,
        };

        await policy.CacheRequestAsync(context, CancellationToken.None);
        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
        context.AllowCacheLookup.Should().BeTrue();
        context.AllowCacheStorage.Should().BeTrue();
    }
}
