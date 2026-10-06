// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Protocols.Terrain;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Terrain;

public sealed class TerrainCacheHeaderTests
{
    [Theory]
    [InlineData("Authorization", "Bearer opaque")]
    [InlineData("X-API-Key", "opaque")]
    public void SRV_INF_011_CredentialedRequest_IsPrivateAndVariesByCredentials(string name, string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[name] = value;

        TerrainEndpoints.SetCacheHeader(context, 300);

        context.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=300");
        context.Response.Headers.Vary.ToString().Should().Be("Authorization, X-API-Key");
    }
}
