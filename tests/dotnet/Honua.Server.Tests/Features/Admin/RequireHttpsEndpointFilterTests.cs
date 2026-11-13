// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Admin;

public sealed class RequireHttpsEndpointFilterTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("http", false)]
    [InlineData("https", true)]
    public async Task InvokeAsync_OnlyHttpsReachesHandler_AndNeverCaches(string scheme, bool reachesHandler)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        var called = false;
        var result = await new RequireHttpsEndpointFilter().InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context), _ => { called = true; return ValueTask.FromResult<object?>("handled"); });

        called.Should().Be(reachesHandler);
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        if (reachesHandler)
        {
            result.Should().Be("handled");
        }
        else
        {
            result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        }
    }
}
