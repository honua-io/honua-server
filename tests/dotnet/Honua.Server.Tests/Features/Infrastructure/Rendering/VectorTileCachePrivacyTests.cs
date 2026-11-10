// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

public sealed class VectorTileCachePrivacyTests
{
    [UnitTheory]
    [InlineData(null, null, "public, max-age=90, s-maxage=90, stale-if-error=86400")]
    [InlineData("dev-bypass", null, SharedCacheGuard.NotShared)]
    [InlineData("api-key", null, SharedCacheGuard.NotShared)]
    [InlineData(null, "Authorization", SharedCacheGuard.NotShared)]
    [InlineData(null, "X-API-Key", SharedCacheGuard.NotShared)]
    [InlineData("dev-bypass", "X-API-Key", SharedCacheGuard.NotShared)]
    public void UseTilesetLifetime_CredentialsAndDevelopmentBypass_AreNeverSharedAndAnonymousGetsTheTilesetTtl(
        string? authenticationType, string? credentialHeader, string expected)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Method = HttpMethods.Get;
        context.SetEndpoint(new Endpoint(
            null, new EndpointMetadataCollection(new SharedCacheClassMetadata(ResponseCacheClass.Tile)), "tile"));
        if (authenticationType is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("auth_type", authenticationType)], authenticationType));
        }

        if (credentialHeader is not null)
        {
            context.Request.Headers[credentialHeader] = "test-credential";
        }

        var options = new TileOptions
        {
            CacheMaxAge = 3600,
            TilesetLifecycle = new Dictionary<string, TilesetCacheLifecycle>
            {
                [TilesetTtlResolver.BuildKey("service", "layer", "WebMercatorQuad")] =
                    new() { TtlSeconds = 90 }
            }
        };

        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        VectorTileExecution.UseTilesetLifetime(context, options, "service", "layer", 41);
        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers[HeaderNames.CacheControl].ToString().Should().Be(expected);
    }
}
