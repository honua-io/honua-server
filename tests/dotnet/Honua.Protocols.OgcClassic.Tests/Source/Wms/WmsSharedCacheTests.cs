// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Collection("Database")]
public sealed class WmsSharedCacheTests : OgcClassicAuthorizationProofTestBase
{
    private const string WmsEndpoint = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&VERSION=1.3.0";
    private const string WmtsEndpoint = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&VERSION=1.0.0";
    private const string TileCacheControl =
        "public, max-age=60, s-maxage=300, stale-while-revalidate=60, stale-if-error=86400";
    private const string GetMapQuery =
        "&REQUEST=GetMap&BBOX=-90,-180,90,180&CRS=EPSG:4326&WIDTH=64&HEIGHT=64&STYLES=&FORMAT=image/png&LAYERS=0";

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task PublicWmsGetMap_EmitsSharedTileCacheControl()
    {
        ConfigurePublicService();

        using var response = await Fixture.Client.GetAsync(WmsEndpoint + GetMapQuery);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Be(TileCacheControl);
        response.Headers.Vary.Should().Contain(["Origin", "Accept", "Accept-Encoding"]);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task PublicWmsGetCapabilities_EmitsSharedMetadataCacheControl()
    {
        ConfigurePublicService();

        using var response = await Fixture.Client.GetAsync(WmsEndpoint + "&REQUEST=GetCapabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=30, s-maxage=60");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    public async Task PublicWmsGetFeatureInfo_EmitsSharedFeatureQueryCacheControl()
    {
        ConfigurePublicService();

        using var response = await Fixture.Client.GetAsync(
            WmsEndpoint +
            "&REQUEST=GetFeatureInfo&BBOX=-90,-180,90,180&CRS=EPSG:4326&WIDTH=64&HEIGHT=64&STYLES=&LAYERS=0&QUERY_LAYERS=0&INFO_FORMAT=text/plain&I=32&J=32");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Be(
            "public, max-age=0, s-maxage=30, stale-while-revalidate=30, stale-if-error=86400");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task AuthenticatedOrSecuredWmsGetMap_RemainsPrivateNoStore()
    {
        ConfigurePublicService();

        using var authenticated = await Viewer().GetAsync(WmsEndpoint + GetMapQuery);
        authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNotShared(authenticated);

        Fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowAnonymousWrite = false });

        using var refused = await Fixture.Client.GetAsync(WmsEndpoint + GetMapQuery);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNotShared(refused);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task WmsImageExceptionOnPublicService_IsNeverSharedCached()
    {
        ConfigurePublicService();

        using var response = await Fixture.Client.GetAsync(
            WmsEndpoint +
            "&REQUEST=GetMap&BBOX=-90,-180,90,180&CRS=EPSG:4326&WIDTH=64&HEIGHT=64&STYLES=&FORMAT=image/png&LAYERS=does_not_exist&EXCEPTIONS=INIMAGE");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNotShared(response);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    public async Task PublicWmtsGetTile_EmitsSharedTileCacheControl()
    {
        ConfigurePublicService();

        using var response = await Fixture.Client.GetAsync(
            WmtsEndpoint +
            "&REQUEST=GetTile&LAYER=0&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&FORMAT=image/png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Be(TileCacheControl);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS/{**restPath}")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    public async Task PublicRestfulWmtsTile_EmitsSharedTileCacheControl()
    {
        ConfigurePublicService();

        using var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS/{WebAppFixture.TestLayerId}/default/WebMercatorQuad/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Be(TileCacheControl);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task PublicWmsGetMapOnServiceAlias_EmitsSharedTileCacheControl()
    {
        ConfigurePublicService();

        using var response = await Fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wms?SERVICE=WMS&VERSION=1.3.0" + GetMapQuery);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Be(TileCacheControl);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    public async Task SecuredZeroLayerWmtsGetCapabilities_IsRefusedAndNotSharedCached()
    {
        Fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowAnonymousWrite = false });
        var snapshot = Fixture.GetCurrentV2GraphSnapshot();
        var graphWithoutLayers = snapshot.Graph with { Publications = [] };
        ((TestMetadataV2GraphProvider)Fixture.GetService<IMetadataV2GraphProvider>())
            .SetGraph(graphWithoutLayers, schema: Fixture.CurrentSchema);

        using var response = await Fixture.Client.GetAsync(WmtsEndpoint + "&REQUEST=GetCapabilities");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNotShared(response);
    }

    private static void AssertNotShared(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        cacheControl.Should().NotBeNull();
        cacheControl!.Private.Should().BeTrue();
        cacheControl.NoStore.Should().BeTrue();
        cacheControl.Public.Should().BeFalse();
        cacheControl.SharedMaxAge.Should().BeNull();
    }

    private void ConfigurePublicService()
        => Fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = false });
}
