// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class SharedCachePolicyTests
{
    [Theory]
    [InlineData(ResponseCacheClass.Tile, "public, max-age=60, s-maxage=300, stale-while-revalidate=60, stale-if-error=86400")]
    [InlineData(ResponseCacheClass.FeatureQuery, "public, max-age=0, s-maxage=30, stale-while-revalidate=30, stale-if-error=86400")]
    [InlineData(ResponseCacheClass.StaticMapImage, "public, max-age=0, s-maxage=60, stale-while-revalidate=30")]
    [InlineData(ResponseCacheClass.StaticObject, "public, max-age=60, s-maxage=300")]
    public void Apply_AnonymousGrantOnDeclaredRoute_WritesProductDefault(ResponseCacheClass cacheClass, string expected)
    {
        var context = AnonymousGet(cacheClass);
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(expected);
    }

    [Fact]
    public void Apply_MetadataDocumentWithoutGrant_IsSharedBecauseItListsOnlyWhatAnonymousCallersMayRead()
    {
        var context = AnonymousGet(ResponseCacheClass.MetadataDocument);

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=30, s-maxage=60");
        context.Response.Headers.Vary.ToString().Should().Contain("Accept-Language");
    }

    [Theory]
    [InlineData(ResponseCacheClass.Tile)]
    [InlineData(ResponseCacheClass.FeatureQuery)]
    [InlineData(ResponseCacheClass.StaticMapImage)]
    [InlineData(ResponseCacheClass.StaticObject)]
    public void Apply_AccessNeverDecided_IsNotShared(ResponseCacheClass cacheClass)
    {
        var context = AnonymousGet(cacheClass);

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [Theory]
    [InlineData(ResponseCacheClass.Tile)]
    [InlineData(ResponseCacheClass.MetadataDocument)]
    public void Apply_AccessDenied_IsNotSharedEvenWhenAnEarlierLayerWasGranted(ResponseCacheClass cacheClass)
    {
        var context = AnonymousGet(cacheClass);
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        ResponseAccessVerdict.Record(context, AccessDecision.RequiresAuth());

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [Fact]
    public void Record_AuthenticatedCallerGranted_IsRestricted()
    {
        var context = AnonymousGet(ResponseCacheClass.Tile);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "editor")], "Bearer"));

        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());

        ResponseAccessVerdict.Restricted(context).Should().BeTrue();
        ResponseAccessVerdict.GrantedToAnonymous(context).Should().BeFalse();
    }

    [Fact]
    public void Apply_HandlerForbadeStorage_KeepsTheRefusal()
    {
        var context = GrantedTile();
        context.Response.Headers.CacheControl = "no-store";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public void Apply_ETagFallbackAlreadyWritten_IsReplacedByThePublicPolicy()
    {
        var context = GrantedTile();
        context.Response.Headers.CacheControl = "private, must-revalidate";

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [Fact]
    public void Apply_TilesetLifetimeConfigured_OverridesTheProductDefault()
    {
        var context = GrantedTile();
        var tileOptions = new TileOptions
        {
            TilesetLifecycle = new() { ["parcels/3/WebMercatorQuad"] = new TilesetCacheLifecycle { TtlSeconds = 90 } },
        };

        SharedCacheResponse.UseTileset(context, tileOptions, new TilesetAddress("parcels", "3", "WebMercatorQuad"));
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=90, s-maxage=90, stale-if-error=86400");
    }

    [Fact]
    public void Apply_GlobalTileMaxAgeOnly_KeepsTheProductDefault()
    {
        var context = GrantedTile();

        SharedCacheResponse.UseTileset(context, new TileOptions { CacheMaxAge = 3600 }, new TilesetAddress("parcels", "3", "WebMercatorQuad"));
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "parcels/3")]
    public void Apply_EmergencyCoversTheLayer_ForcesFifteenSecondsAtTheCdnOverTheLayerLifetime(bool allPublicLayers, string? namedLayer)
    {
        var emergency = new SharedCacheEmergency { AllPublicLayers = allPublicLayers };
        if (namedLayer is not null)
        {
            emergency.Layers.Add(namedLayer);
        }

        var context = GrantedTile(new SharedCacheOptions { Emergency = emergency });
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("parcels", "3"));
        SharedCacheResponse.UseLifetime(context, SharedCacheLifetime.ForBrowserAndCdn(7200));

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=0, s-maxage=15, stale-if-error=86400");
    }

    [Fact]
    public void Apply_EmergencyNamesAnotherLayer_LeavesThisLayerAlone()
    {
        var emergency = new SharedCacheEmergency();
        emergency.Layers.Add("roads/1");
        var context = GrantedTile(new SharedCacheOptions { Emergency = emergency });
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("parcels", "3"));

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public void Guard_ErrorStatus_IsNeverShared(int statusCode)
    {
        var context = GrantedTile();
        SharedCacheResponse.Apply(context);
        context.Response.StatusCode = statusCode;

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [Fact]
    public void Guard_NoCacheControlAtAll_WritesTheSafeDefault()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers.ETag = "\"abc\"";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [Fact]
    public void Guard_ResponseSetsACookie_IsNotShared()
    {
        var context = GrantedTile();
        SharedCacheResponse.Apply(context);
        context.Response.Headers.SetCookie = "session=1";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [Fact]
    public void Apply_SubjectNotDeclared_IsReadFromTheRoute()
    {
        var emergency = new SharedCacheEmergency();
        emergency.Layers.Add("parcels/3");
        var context = GrantedTile(new SharedCacheOptions { Emergency = emergency });
        context.Request.RouteValues["serviceId"] = "parcels";
        context.Request.RouteValues["layerId"] = "3";

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=0, s-maxage=15");
    }

    [Fact]
    public void Apply_RolloutInProgress_StopsTheCdnHoldingMetadataButNotTiles()
    {
        var metadata = AnonymousGet(ResponseCacheClass.MetadataDocument, rolloutWindowOpen: true);
        var tile = GrantedTile(rolloutWindowOpen: true);

        SharedCacheResponse.Apply(metadata);
        SharedCacheResponse.Apply(tile);

        metadata.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=30, s-maxage=0");
        tile.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [Theory]
    [InlineData("X-Honua-Job-Id")]
    [InlineData("X-API-Key")]
    public void Apply_RequestCarriesACredentialHeader_WritesNothingPublic(string header)
    {
        var context = GrantedTile();
        context.Request.Headers[header] = "credential";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    private static DefaultHttpContext GrantedTile(SharedCacheOptions? options = null, bool rolloutWindowOpen = false)
    {
        var context = AnonymousGet(ResponseCacheClass.Tile, options, rolloutWindowOpen);
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        return context;
    }

    private static DefaultHttpContext AnonymousGet(
        ResponseCacheClass cacheClass, SharedCacheOptions? options = null, bool rolloutWindowOpen = false)
    {
        var services = new ServiceCollection();
        services.AddOptions<SharedCacheOptions>().Configure(configured => configured.Emergency = (options ?? new SharedCacheOptions()).Emergency);
        services.AddSingleton<IRolloutWindow>(new FixedRolloutWindow(rolloutWindowOpen));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Get;
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new SharedCacheClassMetadata(cacheClass)), "declared"));
        return context;
    }

    private sealed class FixedRolloutWindow(bool isOpen) : IRolloutWindow
    {
        public bool IsOpen => isOpen;
    }
}
