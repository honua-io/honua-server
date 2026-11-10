// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class SharedCachePolicyTests
{
    [UnitTheory]
    [InlineData(nameof(ResponseCacheClass.Tile), "public, max-age=60, s-maxage=300, stale-while-revalidate=60, stale-if-error=86400")]
    [InlineData(nameof(ResponseCacheClass.FeatureQuery), "public, max-age=0, s-maxage=30, stale-while-revalidate=30, stale-if-error=86400")]
    [InlineData(nameof(ResponseCacheClass.StaticMapImage), "public, max-age=0, s-maxage=60, stale-while-revalidate=30")]
    [InlineData(nameof(ResponseCacheClass.StaticObject), "public, max-age=60, s-maxage=300")]
    public void Apply_AnonymousGrantOnDeclaredRoute_WritesProductDefault(string cacheClassName, string expected)
    {
        var context = AnonymousGet(Enum.Parse<ResponseCacheClass>(cacheClassName));
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(expected);
    }

    [UnitTest]
    public void Apply_MetadataDocumentWithoutGrant_IsSharedBecauseItListsOnlyWhatAnonymousCallersMayRead()
    {
        var context = AnonymousGet(ResponseCacheClass.MetadataDocument);

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=30, s-maxage=60");
        context.Response.Headers.Vary.ToString().Should().Contain("Accept-Language");
    }

    [UnitTheory]
    [InlineData(nameof(ResponseCacheClass.Tile))]
    [InlineData(nameof(ResponseCacheClass.FeatureQuery))]
    [InlineData(nameof(ResponseCacheClass.StaticMapImage))]
    [InlineData(nameof(ResponseCacheClass.StaticObject))]
    public void Apply_AccessNeverDecided_IsNotShared(string cacheClassName)
    {
        var context = AnonymousGet(Enum.Parse<ResponseCacheClass>(cacheClassName));

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTheory]
    [InlineData(nameof(ResponseCacheClass.Tile))]
    [InlineData(nameof(ResponseCacheClass.MetadataDocument))]
    public void Apply_AccessDenied_IsNotSharedEvenWhenAnEarlierLayerWasGranted(string cacheClassName)
    {
        var context = AnonymousGet(Enum.Parse<ResponseCacheClass>(cacheClassName));
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        ResponseAccessVerdict.Record(context, AccessDecision.RequiresAuth());

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void ObserveGrant_HandlerFiltersByAccessWithoutRefusing_GrantCountsAndDenialDoesNotRefuseTheRequest()
    {
        var context = AnonymousGet(ResponseCacheClass.Tile);

        ResponseAccessVerdict.ObserveGrant(context, AccessDecision.RequiresAuth());
        ResponseAccessVerdict.ObserveGrant(context, AccessDecision.Allowed());

        ResponseAccessVerdict.GrantedToAnonymous(context).Should().BeTrue();
        ResponseAccessVerdict.Restricted(context).Should().BeFalse();
    }

    [UnitTest]
    public void Apply_Tile_VariesOnAcceptBecauseANegotiatedTileIsPngOrVectorByAccept()
    {
        var context = GrantedTile();

        SharedCacheResponse.Apply(context);

        context.Response.Headers.Vary.ToString().Should().Contain("Accept").And.Contain("Origin").And.Contain("Accept-Encoding");
    }

    [UnitTest]
    public void ExcludeIfAdHocSpatial_SpatialFilter_IsNotShared()
    {
        var context = GrantedQuery();
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
        var context = GrantedQuery();

        SharedCacheResponse.ExcludeIfAdHocSpatial(context, new FeatureQuery(), "S_INTERSECTS(geometry, POINT(1 2))");
        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void ExcludeIfAdHocSpatial_AttributeOnlyQuery_StaysShared()
    {
        var context = GrantedQuery();

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
        var context = GrantedTile(new SharedCacheOptions { Emergency = emergency });
        var service = new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Name = "parcels" } };
        var publication = new MetadataV2Publication { LayerIndex = 3 };

        SharedCacheResponse.UsePublication(context, service, publication);
        SharedCacheResponse.UseTileset(context, new TileOptions(), new TilesetAddress("FeatureServer", "3", "WebMercatorQuad"));
        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith(expectedPrefix);
    }

    [UnitTest]
    public void Record_AuthenticatedCallerGranted_IsRestricted()
    {
        var context = AnonymousGet(ResponseCacheClass.Tile);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "editor")], "Bearer"));

        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());

        ResponseAccessVerdict.Restricted(context).Should().BeTrue();
        ResponseAccessVerdict.GrantedToAnonymous(context).Should().BeFalse();
    }

    [UnitTest]
    public void Apply_HandlerForbadeStorage_KeepsTheRefusal()
    {
        var context = GrantedTile();
        context.Response.Headers.CacheControl = "no-store";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Apply_ETagFallbackAlreadyWritten_IsReplacedByThePublicPolicy()
    {
        var context = GrantedTile();
        context.Response.Headers.CacheControl = "private, must-revalidate";

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [UnitTest]
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

    [UnitTest]
    public void Apply_GlobalTileMaxAgeOnly_KeepsTheProductDefault()
    {
        var context = GrantedTile();

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

        var context = GrantedTile(new SharedCacheOptions { Emergency = emergency });
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
        var context = GrantedTile(new SharedCacheOptions { Emergency = emergency });
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("parcels", "3"));

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [UnitTheory]
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

    [UnitTest]
    public void Guard_NoCacheControlAtAll_WritesTheSafeDefault()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers.ETag = "\"abc\"";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_RefusalThatAlsoCarriesALifetime_LeavesAsExactlyOneRefusal()
    {
        var context = GrantedTile();
        context.Response.Headers.CacheControl = "no-store, max-age=3600, private";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_GeoServicesRefusalSentAs200WithNoCacheControl_IsNotShared()
    {
        var context = AnonymousGet(ResponseCacheClass.Tile);
        ResponseAccessVerdict.Record(context, AccessDecision.RequiresAuth());
        BypassOutputCacheOnErrorEnvelopePolicy.MarkErrorEnvelope(context, 499);
        context.Response.StatusCode = StatusCodes.Status200OK;

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_ErrorEnvelopeOnAnOtherwisePublicResponse_IsNotShared()
    {
        var context = GrantedTile();
        SharedCacheResponse.Apply(context);
        BypassOutputCacheOnErrorEnvelopePolicy.MarkErrorEnvelope(context, 500);

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Guard_ReplayedResponseStatesAnAge_TheAgeIsRemoved()
    {
        var context = GrantedTile();
        SharedCacheResponse.Apply(context);
        context.Response.Headers.Age = "288";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.ContainsKey("Age").Should().BeFalse();
        context.Response.Headers.CacheControl.ToString().Should().StartWith("public, max-age=60, s-maxage=300");
    }

    [UnitTest]
    public void Guard_ResponseSetsACookie_IsNotShared()
    {
        var context = GrantedTile();
        SharedCacheResponse.Apply(context);
        context.Response.Headers.SetCookie = "session=1";

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
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

    [UnitTheory]
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

    [UnitTest]
    public void Apply_LayerLifetimeConfigured_ReplacesTheTilesetLifetimeAndTheProductDefault()
    {
        var options = new SharedCacheOptions();
        options.Layers["parcels/3"] = new SharedCacheLifetime { MaxAgeSeconds = 0, SharedMaxAgeSeconds = 20 };
        var context = GrantedTile(options);
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("parcels", "3"));
        SharedCacheResponse.UseLifetime(context, SharedCacheLifetime.ForBrowserAndCdn(7200));

        SharedCacheResponse.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=0, s-maxage=20, stale-if-error=86400");
    }

    [UnitTest]
    public void Apply_ForwardedClientCertificateUnderARenamedHeader_IsNotShared()
    {
        var context = GrantedTile();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:ClientCertificates:ForwardedCertificate:HeaderName"] = "X-Edge-Client-Cert",
                })
                .Build())
            .BuildServiceProvider();
        context.Request.Headers["X-Edge-Client-Cert"] = "credential";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Apply_MutualTlsConnection_IsNotShared()
    {
        var context = GrantedTile();
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        context.Connection.ClientCertificate = certificate;

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    [UnitTest]
    public void Apply_EdgeMarkedTheRequestAsCacheBypass_IsNotShared()
    {
        var context = GrantedTile();
        context.Request.Headers.Cookie = "honua_cache_bypass=1";

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
    }

    private static DefaultHttpContext GrantedQuery()
    {
        var context = AnonymousGet(ResponseCacheClass.FeatureQuery);
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        return context;
    }

    private static DefaultHttpContext GrantedTile(SharedCacheOptions? options = null)
    {
        var context = AnonymousGet(ResponseCacheClass.Tile, options);
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        return context;
    }

    private static DefaultHttpContext AnonymousGet(
        ResponseCacheClass cacheClass, SharedCacheOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddOptions<SharedCacheOptions>().Configure(configured =>
        {
            configured.Emergency = (options ?? configured).Emergency;
            configured.Layers = (options ?? configured).Layers;
        });
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Get;
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new SharedCacheClassMetadata(cacheClass)), "declared"));
        return context;
    }
}
