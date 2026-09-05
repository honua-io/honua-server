// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

public sealed class VectorTileCachePrivacyTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("dev-bypass", null, false)]
    [InlineData("api-key", null, true)]
    [InlineData(null, "Authorization", true)]
    [InlineData(null, "X-API-Key", true)]
    [InlineData("dev-bypass", "Authorization", true)]
    [InlineData("dev-bypass", "X-API-Key", true)]
    public void ApplyCacheHeaders_CredentialsAndDevelopmentBypass_PreservePrivacyAndTilesetTtl(
        string? authenticationType, string? credentialHeader, bool privateCache)
    {
        var context = new DefaultHttpContext();
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

        VectorTileExecution.ApplyCacheHeaders(context, options, "service", "layer", 41);

        context.Response.Headers[HeaderNames.CacheControl].ToString()
            .Should().Be($"{(privateCache ? "private" : "public")}, max-age=90");
        context.Response.Headers[HeaderNames.Vary].ToString()
            .Should().Be(privateCache ? "Authorization, X-API-Key" : string.Empty);
    }
}
