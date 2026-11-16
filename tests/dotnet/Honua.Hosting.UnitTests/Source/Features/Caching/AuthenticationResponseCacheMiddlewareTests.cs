// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Hosting.UnitTests.Features.Caching;

public sealed class AuthenticationResponseCacheMiddlewareTests
{
    [UnitTheory]
    [InlineData("anonymous", "no-store")]
    [InlineData("anonymous", "no-store, no-transform")]
    [InlineData("authenticated", "no-store")]
    [InlineData("authenticated", "no-store, no-transform")]
    public void ApplyFinalPolicy_PreservesTransformationRefusalWhilePreventingStorage(string actor, string responsePolicy)
    {
        var context = new DefaultHttpContext();
        if (actor == "authenticated")
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subscriber")], "Bearer"));
        }

        context.Response.Headers.CacheControl = responsePolicy;

        AuthenticationResponseCacheMiddleware.ApplyFinalPolicy(context);

        var cacheControl = context.Response.GetTypedHeaders().CacheControl!;
        cacheControl.NoTransform.Should().Be(responsePolicy.Contains("no-transform", StringComparison.Ordinal));
        cacheControl.NoStore.Should().BeTrue();
        cacheControl.Private.Should().BeTrue();
        cacheControl.Public.Should().BeFalse();
        cacheControl.MaxAge.Should().BeNull();
        cacheControl.SharedMaxAge.Should().BeNull();
    }
}
