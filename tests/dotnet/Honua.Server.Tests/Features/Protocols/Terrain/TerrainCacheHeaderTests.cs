// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Protocols.Terrain;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Terrain;

public sealed class TerrainCacheHeaderTests
{
    [UnitTheory]
    [InlineData("Authorization", "Bearer opaque")]
    [InlineData("X-API-Key", "opaque")]
    [InlineData("X-Esri-Authorization", "Bearer opaque")]
    [InlineData("X-Honua-Embed-Key", "opaque")]
    [InlineData("X-Honua-Token", "opaque")]
    public void SRV_INF_011_CredentialedRequest_IsPrivateAndVariesByCredentials(string name, string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[name] = value;

        TerrainEndpoints.SetCacheHeader(context, 300);

        context.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=300");
        context.Response.Headers.Vary.ToString().Should().Be(
            "Authorization, X-API-Key, X-Esri-Authorization, X-Honua-Embed-Key, X-Honua-Token, Cookie");
    }

    [UnitTest]
    public void SRV_INF_011_AnonymousRequest_IsPublicWithoutCredentialVary()
    {
        var context = new DefaultHttpContext();

        TerrainEndpoints.SetCacheHeader(context, 300);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=300");
        context.Response.Headers.Vary.ToString().Should().BeEmpty();
    }
}
