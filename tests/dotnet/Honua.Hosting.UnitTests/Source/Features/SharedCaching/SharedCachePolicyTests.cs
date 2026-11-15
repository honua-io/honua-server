// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class SharedCachePolicyTests
{
    [UnitTheory]
    [InlineData(nameof(ResponseCacheClass.Tile), "public, max-age=60, s-maxage=300, stale-while-revalidate=60, stale-if-error=86400")]
    [InlineData(nameof(ResponseCacheClass.FeatureQuery), "public, max-age=0, s-maxage=30, stale-while-revalidate=30, stale-if-error=86400")]
    [InlineData(nameof(ResponseCacheClass.StaticObject), "public, max-age=60, s-maxage=300")]
    public void Apply_AnonymousGrantOnDeclaredRoute_WritesProductDefault(string cacheClassName, string expected)
    {
        var context = SharedCacheTestContexts.AnonymousGet(Enum.Parse<ResponseCacheClass>(cacheClassName));
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(expected);
    }

    [UnitTest]
    public void Apply_MetadataDocumentWithoutGrant_IsSharedBecauseItListsOnlyWhatAnonymousCallersMayRead()
    {
        var context = SharedCacheTestContexts.AnonymousGet(ResponseCacheClass.MetadataDocument);

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=30, s-maxage=60");
        context.Response.Headers.Vary.ToString().Should().Contain("Accept-Language");
    }

    [UnitTest]
    public void ObserveGrant_HandlerFiltersByAccessWithoutRefusing_GrantCountsAndDenialDoesNotRefuseTheRequest()
    {
        var context = SharedCacheTestContexts.AnonymousGet(ResponseCacheClass.Tile);

        ResponseAccessVerdict.ObserveGrant(context, AccessDecision.RequiresAuth());
        ResponseAccessVerdict.ObserveGrant(context, AccessDecision.Allowed());

        ResponseAccessVerdict.GrantedToAnonymous(context).Should().BeTrue();
        ResponseAccessVerdict.Restricted(context).Should().BeFalse();
    }

    [UnitTest]
    public void Apply_Tile_VariesOnAcceptBecauseANegotiatedTileIsPngOrVectorByAccept()
    {
        var context = SharedCacheTestContexts.GrantedTile();

        SharedCacheResponse.Apply(context);

        context.Response.Headers.Vary.ToString().Should().Contain("Accept").And.Contain("Origin").And.Contain("Accept-Encoding");
    }

    [UnitTest]
    public void ExcludeIfAdHocSpatial_SpatialFilter_IsNotShared()
    {
        var context = SharedCacheTestContexts.GrantedQuery();
        var spatial = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.Intersects, 4326),
        };

        var excluded = SharedCacheResponse.ExcludeIfAdHocSpatial(context, spatial, filterText: null);
        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        excluded.Should().BeTrue();
        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void ExcludeIfAdHocSpatial_Cql2SpatialPredicate_IsNotShared()
    {
        var context = SharedCacheTestContexts.GrantedQuery();

        SharedCacheResponse.ExcludeIfAdHocSpatial(context, new FeatureQuery(), "S_INTERSECTS(geometry, POINT(1 2))");
        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void ExcludeIfAdHocSpatial_AttributeOnlyQuery_StaysShared()
    {
        var context = SharedCacheTestContexts.GrantedQuery();

        var excluded = SharedCacheResponse.ExcludeIfAdHocSpatial(context, new FeatureQuery(), "population > 1000");
        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        excluded.Should().BeFalse();
        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=0, s-maxage=30");
    }

    [UnitTheory]
    [InlineData("parcels/3", "public, max-age=0, s-maxage=15")]
    [InlineData("FeatureServer/3", "public, max-age=60, s-maxage=300")]
    public void UseTileset_ProtocolTilesetKey_DoesNotReplaceThePublicationSubject(string emergencyLayer, string expectedPrefix)
    {
        var emergency = new SharedCacheEmergency();
        emergency.Layers.Add(emergencyLayer);
        var context = SharedCacheTestContexts.GrantedTile(new SharedCacheOptions { Emergency = emergency });
        var service = new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Name = "parcels" } };
        var publication = new MetadataV2Layer { LayerIndex = 3 };

        SharedCacheResponse.UsePublication(context, service, publication);
        SharedCacheResponse.UseTileset(context, new TileOptions(), new TilesetAddress("FeatureServer", "3", "WebMercatorQuad"));
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith(expectedPrefix);
    }

    [UnitTest]
    public void Apply_ETagFallbackAlreadyWritten_IsReplacedByThePublicPolicy()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        context.Response.Headers.CacheControl = "private, must-revalidate";

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [UnitTest]
    public void Apply_TilesetLifetimeConfigured_OverridesTheProductDefault()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        var tileOptions = new TileOptions
        {
            TilesetLifecycle = new() { ["parcels/3/WebMercatorQuad"] = new TilesetCacheLifecycle { TtlSeconds = 90 } },
        };

        SharedCacheResponse.UseTileset(context, tileOptions, new TilesetAddress("parcels", "3", "WebMercatorQuad"));
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=90, s-maxage=90, stale-if-error=86400");
    }

    [UnitTest]
    public void Apply_GlobalTileMaxAgeOnly_KeepsTheProductDefault()
    {
        var context = SharedCacheTestContexts.GrantedTile();

        SharedCacheResponse.UseTileset(context, new TileOptions { CacheMaxAge = 3600 }, new TilesetAddress("parcels", "3", "WebMercatorQuad"));
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [UnitTheory]
    [InlineData(true, null)]
    [InlineData(false, "parcels/3")]
    public void Apply_EmergencyCoversTheLayer_ForcesFifteenSecondsAtTheCdnOverTheLayerLifetime(bool allPublicLayers, string? namedLayer)
    {
        var emergency = new SharedCacheEmergency { AllPublicLayers = allPublicLayers };
        if (namedLayer is not null)
        {
            emergency.Layers.Add(namedLayer);
        }

        var context = SharedCacheTestContexts.GrantedTile(new SharedCacheOptions { Emergency = emergency });
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("parcels", "3"));
        SharedCacheResponse.UseLifetime(context, SharedCacheLifetime.ForBrowserAndCdn(7200));

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=0, s-maxage=15, stale-if-error=86400");
    }

    [UnitTest]
    public void Apply_EmergencyNamesAnotherLayer_LeavesThisLayerAlone()
    {
        var emergency = new SharedCacheEmergency();
        emergency.Layers.Add("roads/1");
        var context = SharedCacheTestContexts.GrantedTile(new SharedCacheOptions { Emergency = emergency });
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("parcels", "3"));

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [UnitTest]
    public void Apply_SubjectNotDeclared_IsReadFromTheRoute()
    {
        var emergency = new SharedCacheEmergency();
        emergency.Layers.Add("parcels/3");
        var context = SharedCacheTestContexts.GrantedTile(new SharedCacheOptions { Emergency = emergency });
        context.Request.RouteValues["serviceId"] = "parcels";
        context.Request.RouteValues["layerId"] = "3";

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=0, s-maxage=15");
    }

    [UnitTest]
    public void Apply_LayerLifetimeConfigured_ReplacesTheTilesetLifetimeAndTheProductDefault()
    {
        var options = new SharedCacheOptions();
        options.Layers["parcels/3"] = new SharedCacheLifetime { MaxAgeSeconds = 0, SharedMaxAgeSeconds = 20 };
        var context = SharedCacheTestContexts.GrantedTile(options);
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("parcels", "3"));
        SharedCacheResponse.UseLifetime(context, SharedCacheLifetime.ForBrowserAndCdn(7200));

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=0, s-maxage=20, stale-if-error=86400");
    }
}
