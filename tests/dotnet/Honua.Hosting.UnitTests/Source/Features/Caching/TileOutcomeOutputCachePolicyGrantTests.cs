// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class TileOutcomeOutputCachePolicyGrantTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_EmptyTileWithoutGrant_IsNotReadmitted()
    {
        var context = EmptyTile(new SharedCacheOptions(), granted: false);

        await new TileOutcomeOutputCachePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_EmptyTileWithZeroCdnLifetime_IsNotReadmitted()
    {
        var options = new SharedCacheOptions { Tile = new SharedCacheLifetime { SharedMaxAgeSeconds = 0 } };
        var context = EmptyTile(options, granted: true);

        await new TileOutcomeOutputCachePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServeResponseAsync_GrantedEmptyTile_IsReadmittedWithinTheCdnLifetime()
    {
        var context = EmptyTile(new SharedCacheOptions(), granted: true);

        await new TileOutcomeOutputCachePolicy().ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeTrue();
        context.ResponseExpirationTimeSpan.Should().Be(TimeSpan.FromSeconds(300));
    }

    private static OutputCacheContext EmptyTile(SharedCacheOptions options, bool granted)
    {
        var services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<SharedCacheOptions>>(new StaticOptionsMonitor(options))
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.RouteValues["collectionId"] = "parcels";
        httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
        httpContext.SetEndpoint(new Endpoint(
            null, new EndpointMetadataCollection(new SharedCacheClassMetadata(ResponseCacheClass.Tile)), "tile"));
        if (granted)
        {
            ResponseAccessVerdict.Record(httpContext, AccessDecision.Allowed());
        }

        return new OutputCacheContext
        {
            HttpContext = httpContext,
            AllowCacheStorage = false,
            ResponseExpirationTimeSpan = TimeSpan.FromHours(1),
        };
    }

    private sealed class StaticOptionsMonitor(SharedCacheOptions value) : IOptionsMonitor<SharedCacheOptions>
    {
        public SharedCacheOptions CurrentValue => value;

        public SharedCacheOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SharedCacheOptions, string?> listener) => null;
    }
}
