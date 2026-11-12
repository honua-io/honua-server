// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Hosting.ComponentTests;

[Trait("Tier", "Integration")]
public sealed class OutputCacheStatusTests
{
    [Fact]
    public async Task CachedRoute_FirstAndSecondRead_ReportMissThenHitWithRevision()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var first = await client.GetAsync("/ogc/collections/parcels/tiles/0/0/0");
        using var second = await client.GetAsync("/ogc/collections/parcels/tiles/0/0/0");

        first.Headers.GetValues(OutputCacheStatus.HeaderName).Single()
            .Should().Be("Honua; fwd=miss; detail=\"revision=revision-one\"");
        second.Headers.GetValues(OutputCacheStatus.HeaderName).Single()
            .Should().MatchRegex("^Honua; hit; ttl=(29[0-9]|300); detail=\"revision=revision-one\"$");
    }

    [Fact]
    public async Task CachedRoute_CredentialedRead_ReportsBypass()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/collections/parcels/tiles/0/0/0");
        request.Headers.Add("X-API-Key", "key");

        using var response = await client.SendAsync(request);

        response.Headers.GetValues(OutputCacheStatus.HeaderName).Single().Should().StartWith("Honua; fwd=bypass");
    }

    [Fact]
    public async Task UncachedRoute_HasNoCacheStatus()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/plain");

        response.Headers.Contains(OutputCacheStatus.HeaderName).Should().BeFalse();
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOutputCache(options => options.AddBasePolicy(policy =>
        {
            policy.AddPolicy<RouteTagOutputCachePolicy>();
            policy.AddPolicy<BypassOutputCacheOnCredentialedRequestPolicy>();
            policy.AddPolicy<RequireAnonymousGrantOutputCachePolicy>();
            policy.AddPolicy<CapOutputCacheLifetimeAtCdnLifetimePolicy>();
            policy.AddPolicy<OutputCacheStatusPolicy>();
        }, excludeDefaultPolicy: true));
        var provider = Substitute.For<IMetadataV2GraphProvider>();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new MetadataV2GraphSnapshot(new MetadataV2Graph(), "revision-one", DateTimeOffset.UnixEpoch));
        builder.Services.AddSingleton(provider);
        var app = builder.Build();
        app.UseMiddleware<AuthenticationResponseCacheMiddleware>();
        app.UseOutputCache();
        app.MapGet("/ogc/collections/{collectionId}/tiles/{z}/{y}/{x}", (HttpContext context) =>
            {
                ResponseAccessVerdict.Record(context, Honua.Core.Features.Security.Domain.AccessDecision.Allowed());
                return Results.Text("tile");
            })
            .WithSharedCacheClass(ResponseCacheClass.Tile)
            .CacheOutput(policy => policy.Expire(TimeSpan.FromHours(1)));
        app.MapGet("/plain", () => Results.Text("plain"));
        await app.StartAsync();
        return app;
    }
}
