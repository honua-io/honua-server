// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

// A service with one open layer (0) and one role-restricted layer (1). By default the restricted
// layer is hidden from callers who cannot read it; the per-service challenge option names it in
// capabilities and answers requests for it with 401/403 instead of "not defined".
[Collection("Database")]
public sealed class WmsProtectedLayerPresentationTests : OgcClassicAuthorizationProofTestBase
{
    private const string OpenLayer = "0";
    private const string ProtectedLayer = "1";
    private const string Endpoint = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&VERSION=1.3.0";
    private const string GetMapQuery =
        "&REQUEST=GetMap&BBOX=-90,-180,90,180&CRS=EPSG:4326&WIDTH=64&HEIGHT=64&STYLES=&FORMAT=image/png&LAYERS=";

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task DefaultMode_HidesUnreadableLayerFromCapabilitiesAndRequests()
    {
        ConfigureMixedService(mode: null);

        var capabilities = await GetCapabilitiesAsync(Fixture.Client);
        NamedLayers(capabilities).Should().Contain(OpenLayer).And.NotContain(ProtectedLayer);

        using var getMap = await Fixture.Client.GetAsync(Endpoint + GetMapQuery + ProtectedLayer);
        var body = await AssertRefusedWithoutPayloadAsync(getMap, HttpStatusCode.BadRequest);
        body.Should().Contain("code=\"LayerNotDefined\"");
        getMap.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task ChallengeMode_NamesUnreadableLayerWithoutDescribingIt()
    {
        ConfigureMixedService(MetadataV2WmsProtectedLayers.Challenge);

        var capabilities = await GetCapabilitiesAsync(Fixture.Client);
        NamedLayers(capabilities).Should().Contain([OpenLayer, ProtectedLayer]);
        LayerChildren(capabilities, ProtectedLayer).Should().Equal("Name", "Title", "CRS");
        LayerChildren(capabilities, OpenLayer).Should().Contain("EX_GeographicBoundingBox");
        LayerElement(capabilities, ProtectedLayer).Element(Wms("Title"))!.Value.Should().Be(ProtectedLayer);

        var viewerCapabilities = await GetCapabilitiesAsync(Viewer());
        LayerChildren(viewerCapabilities, ProtectedLayer).Should().Contain("EX_GeographicBoundingBox",
            "a caller who can read the layer gets its full description, not the challenge entry");
    }

    [IntegrationTheory]
    [InlineData(GetMapQuery + ProtectedLayer)]
    [InlineData(GetMapQuery + OpenLayer + "," + ProtectedLayer)]
    [InlineData("&REQUEST=GetFeatureInfo&BBOX=-90,-180,90,180&CRS=EPSG:4326&WIDTH=64&HEIGHT=64&STYLES=&LAYERS=0&QUERY_LAYERS=1&INFO_FORMAT=text/plain&I=1&J=1")]
    [InlineData("&REQUEST=GetLegendGraphic&FORMAT=image/png&LAYER=1")]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task ChallengeMode_RequestNamingUnreadableLayer_ChallengesOrDenies(string request)
    {
        ConfigureMixedService(MetadataV2WmsProtectedLayers.Challenge);

        using var anonymous = await Fixture.Client.GetAsync(Endpoint + request);
        var body = await AssertRefusedWithoutPayloadAsync(anonymous, HttpStatusCode.Unauthorized);
        body.Should().Contain("code=\"AccessDenied\"");
        anonymous.Headers.WwwAuthenticate.Should().NotBeEmpty("an unauthenticated caller is told to sign in");

        using var outsider = await Outsider().GetAsync(Endpoint + request);
        (await AssertRefusedWithoutPayloadAsync(outsider, HttpStatusCode.Forbidden)).Should().Contain("code=\"AccessDenied\"");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task ChallengeMode_ReadableRequestsStillSucceed()
    {
        ConfigureMixedService(MetadataV2WmsProtectedLayers.Challenge);

        using var anonymousOpen = await Fixture.Client.GetAsync(Endpoint + GetMapQuery + OpenLayer);
        await AssertImageAsync(anonymousOpen);

        using var viewerProtected = await Viewer().GetAsync(Endpoint + GetMapQuery + ProtectedLayer);
        await AssertImageAsync(viewerProtected);
    }

    [IntegrationTheory]
    [InlineData(GetMapQuery + OpenLayer + "&QUERY_LAYERS=" + ProtectedLayer)]
    [InlineData("&REQUEST=GetLegendGraphic&FORMAT=image/png&LAYER=0&LAYERS=1")]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task ChallengeMode_ParameterTheOperationIgnores_IsNotARequestForTheLayer(string request)
    {
        ConfigureMixedService(MetadataV2WmsProtectedLayers.Challenge);

        using var response = await Fixture.Client.GetAsync(Endpoint + request);
        await AssertImageAsync(response);
    }

    [IntegrationTheory]
    [InlineData("INIMAGE")]
    [InlineData("BLANK")]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task ChallengeMode_ImageExceptionFormat_StillAnswersWithTheChallengeStatus(string exceptions)
    {
        ConfigureMixedService(MetadataV2WmsProtectedLayers.Challenge);

        using var response = await Fixture.Client.GetAsync(Endpoint + GetMapQuery + ProtectedLayer + "&EXCEPTIONS=" + exceptions);
        var body = await AssertRefusedWithoutPayloadAsync(response, HttpStatusCode.Unauthorized);
        body.Should().Contain("code=\"AccessDenied\"");
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
    }

    [IntegrationTheory]
    [InlineData("INIMAGE")]
    [InlineData("BLANK")]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task ServiceWideDenial_ImageExceptionFormat_KeepsDenialStatus(string exceptions)
    {
        // No ConfigureMixedService: the base fixture closes the whole service to the viewer role.
        var url = Endpoint + GetMapQuery + OpenLayer + "&EXCEPTIONS=" + exceptions;

        using var anonymous = await Fixture.Client.GetAsync(url);
        (await AssertRefusedWithoutPayloadAsync(anonymous, HttpStatusCode.Unauthorized)).Should().Contain("code=\"AccessDenied\"");
        anonymous.Headers.WwwAuthenticate.Should().NotBeEmpty();

        using var outsider = await Outsider().GetAsync(url);
        (await AssertRefusedWithoutPayloadAsync(outsider, HttpStatusCode.Forbidden)).Should().Contain("code=\"AccessDenied\"");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms111)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms111, "GetCapabilities")]
    public async Task ChallengeMode_Wms111Capabilities_NameLayerWithOwnSrsOnly()
    {
        ConfigureMixedService(MetadataV2WmsProtectedLayers.Challenge);

        using var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetCapabilities");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var entry = XDocument.Parse(body).Descendants("Layer").Single(layer => layer.Element("Name")?.Value == ProtectedLayer);
        entry.Elements().Select(static element => element.Name.LocalName).Should().Equal("Name", "Title", "SRS");
        entry.Element("SRS")!.Value.Should().Be("EPSG:4326");
    }

    [IntegrationTheory]
    [InlineData(" challenge ")]
    [InlineData("list")]
    [InlineData(true)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    public async Task UnreadableModeValue_KeepsLayerHidden(object mode)
    {
        ConfigureMixedService(mode);

        var capabilities = await GetCapabilitiesAsync(Fixture.Client);
        NamedLayers(capabilities).Should().Contain(OpenLayer).And.NotContain(ProtectedLayer);
        using var getMap = await Fixture.Client.GetAsync(Endpoint + GetMapQuery + ProtectedLayer);
        (await AssertRefusedWithoutPayloadAsync(getMap, HttpStatusCode.BadRequest)).Should().Contain("code=\"LayerNotDefined\"");
    }

    private void ConfigureMixedService(object? mode)
    {
        Fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = false });
        Fixture.UpdateV2ResourceMetadata(
            int.Parse(ProtectedLayer, System.Globalization.CultureInfo.InvariantCulture),
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowAnonymousWrite = false, AllowedRoles = [ViewerRole] });
        if (mode is null)
        {
            return;
        }

        var snapshot = Fixture.GetCurrentV2GraphSnapshot();
        var graph = snapshot.Graph with
        {
            Services = [.. snapshot.Graph.Services.Select(service => IsTestService(service)
                ? service with { Options = WithMode(service.Options, mode) }
                : service)]
        };
        ((TestMetadataV2GraphProvider)Fixture.GetService<IMetadataV2GraphProvider>())
            .SetGraph(graph, schema: Fixture.CurrentSchema);
    }

    private static bool IsTestService(MetadataV2Service service)
        => service.Metadata.Name == WebAppFixture.TestServiceId || service.Metadata.Id == WebAppFixture.TestServiceId;

    private static Dictionary<string, JsonElement> WithMode(IReadOnlyDictionary<string, JsonElement> options, object mode)
        => new(options) { [MetadataV2WmsProtectedLayers.OptionName] = JsonSerializer.SerializeToElement(mode) };

    private static async Task<XDocument> GetCapabilitiesAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Endpoint + "&REQUEST=GetCapabilities");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return XDocument.Parse(body);
    }

    private static XName Wms(string name) => XName.Get(name, "http://www.opengis.net/wms");

    private static IEnumerable<string> NamedLayers(XDocument capabilities)
        => capabilities.Descendants(Wms("Layer")).Select(layer => layer.Element(Wms("Name"))?.Value).OfType<string>();

    private static XElement LayerElement(XDocument capabilities, string name)
        => capabilities.Descendants(Wms("Layer")).Single(layer => layer.Element(Wms("Name"))?.Value == name);

    private static IEnumerable<string> LayerChildren(XDocument capabilities, string name)
        => LayerElement(capabilities, name).Elements().Select(static element => element.Name.LocalName);
}
