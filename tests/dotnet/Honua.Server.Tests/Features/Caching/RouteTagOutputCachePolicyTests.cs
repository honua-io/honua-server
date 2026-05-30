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
/// Unit tests for <see cref="RouteTagOutputCachePolicy"/>.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class RouteTagOutputCachePolicyTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_SceneIdRouteValue_AddsPerSceneTag()
    {
        // Per-scene cache eviction (OutputCacheInvalidationService.InvalidateSceneAsync
        // and the doc contract on /admin/scenes mutations) only works if the
        // scene cache entries actually carry a scene:{sceneId} tag.
        var policy = new RouteTagOutputCachePolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["sceneId"] = "Downtown";

        var context = new OutputCacheContext { HttpContext = httpContext };

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.Tags.Should().Contain("scene:downtown");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_MissingSceneId_DoesNotAddSceneTag()
    {
        var policy = new RouteTagOutputCachePolicy();
        var httpContext = new DefaultHttpContext();

        var context = new OutputCacheContext { HttpContext = httpContext };

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.Tags.Should().NotContain(t => t.StartsWith("scene:", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_KnownRouteValues_AddPerEntityTags()
    {
        var policy = new RouteTagOutputCachePolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["serviceId"] = "TestService";
        httpContext.Request.RouteValues["layerId"] = "1";
        httpContext.Request.RouteValues["collectionId"] = "Roads";
        httpContext.Request.RouteValues["sceneId"] = "alpha";

        var context = new OutputCacheContext { HttpContext = httpContext };

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.Tags.Should().Contain(["service:testservice", "layer:1", "collection:roads", "scene:alpha"]);
    }
}
