// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class RequireAnonymousGrantOutputCachePolicyTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_LayerRouteWithoutRecordedGrant_DoesNotStore()
    {
        var context = LayerRequest();

        await new RequireAnonymousGrantOutputCachePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_LayerRouteGrantedToAnonymous_Stores()
    {
        var context = LayerRequest();
        ResponseAccessVerdict.Record(context.HttpContext, AccessDecision.Allowed());

        await new RequireAnonymousGrantOutputCachePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_LayerRouteGrantedToSignedInCaller_DoesNotStore()
    {
        var context = LayerRequest();
        context.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "editor")], "test"));
        ResponseAccessVerdict.Record(context.HttpContext, AccessDecision.Allowed());

        await new RequireAnonymousGrantOutputCachePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_MetadataDocumentWithoutRestriction_Stores()
    {
        var context = LayerRequest();
        context.HttpContext.SetEndpoint(new Endpoint(
            null,
            new EndpointMetadataCollection(new SharedCacheClassMetadata(ResponseCacheClass.MetadataDocument)),
            "metadata"));

        await new RequireAnonymousGrantOutputCachePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_RouteWithoutResource_Stores()
    {
        var context = new OutputCacheContext { HttpContext = new DefaultHttpContext(), AllowCacheStorage = true };

        await new RequireAnonymousGrantOutputCachePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeTrue();
    }

    private static OutputCacheContext LayerRequest()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.RouteValues["serviceId"] = "parcels";
        httpContext.Request.RouteValues["layerId"] = "0";
        return new OutputCacheContext { HttpContext = httpContext, AllowCacheStorage = true };
    }
}
