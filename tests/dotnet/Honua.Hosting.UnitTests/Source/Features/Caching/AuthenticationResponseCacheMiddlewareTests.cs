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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ApplyFinalPolicy_PreservesTransformationRefusalWhilePreventingStorage(bool authenticated, bool noTransform)
    {
        var context = new DefaultHttpContext();
        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subscriber")], "Bearer"));
        }

        context.Response.Headers.CacheControl = noTransform ? "no-store, no-transform" : "no-store";

        AuthenticationResponseCacheMiddleware.ApplyFinalPolicy(context);

        var cacheControl = context.Response.GetTypedHeaders().CacheControl!;
        cacheControl.NoTransform.Should().Be(noTransform);
        cacheControl.NoStore.Should().BeTrue();
        cacheControl.Private.Should().BeTrue();
        cacheControl.Public.Should().BeFalse();
        cacheControl.MaxAge.Should().BeNull();
        cacheControl.SharedMaxAge.Should().BeNull();
    }
}
