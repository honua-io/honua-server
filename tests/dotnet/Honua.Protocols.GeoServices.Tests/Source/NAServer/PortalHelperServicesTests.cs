// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

/// <summary>
/// The portal's <c>helperServices</c> block (#5035). After <c>SignInToPortal</c>,
/// <c>arcpy.nax</c> reads <c>portals/self</c> and binds routing through
/// <c>helperServices.route</c> and <c>routingUtilities</c>; with the block absent it
/// reports "Cannot use &lt;portal&gt; for network analysis". The utility tasks must then
/// answer under the advertised Routing service id, which publishes no catalog tasks.
/// </summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.NAServer)]
public sealed class PortalHelperServicesTests : IClassFixture<NAServerEndpointTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public PortalHelperServicesTests(NAServerEndpointTestsFixture wrapper)
    {
        _fixture = wrapper.App;
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_AdvertisesTheRoutingHelperServicesTheProviderSupports()
    {
        using var response = await _fixture.Client.GetAsync("/sharing/rest/portals/self?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var helpers = document.RootElement.GetProperty("helperServices");

        var route = helpers.GetProperty("route");
        route.GetProperty("url").GetString().Should().EndWith("/rest/services/Routing/NAServer/Route");
        route.GetProperty("defaultTravelMode").GetString().Should().HaveLength(16);
        helpers.GetProperty("serviceArea").GetProperty("url").GetString().Should().EndWith("/NAServer/ServiceArea");
        helpers.GetProperty("closestFacility").GetProperty("url").GetString().Should().EndWith("/NAServer/ClosestFacility");
        helpers.GetProperty("odCostMatrix").GetProperty("url").GetString().Should().EndWith("/NAServer/ODCostMatrix");
        helpers.GetProperty("routingUtilities").GetProperty("url").GetString().Should().EndWith("/rest/services/Routing/GPServer");
        helpers.TryGetProperty("asyncRoute", out _).Should().BeFalse("Honua publishes no asynchronous routing web tools, so it must not advertise them");

        // The advertised layer resource resolves under that service id, and the
        // portal's default travel mode is the id GetTravelModes publishes.
        using var layer = await _fixture.Client.GetAsync("/rest/services/Routing/NAServer/Route?f=json");
        layer.StatusCode.Should().Be(HttpStatusCode.OK);
        using var modes = await _fixture.Client.GetAsync("/rest/services/Routing/GPServer/GetTravelModes/execute?f=json");
        using var modesDocument = JsonDocument.Parse(await modes.Content.ReadAsStringAsync());
        modesDocument.RootElement.GetProperty("results")[1].GetProperty("value").GetString()
            .Should().Be(route.GetProperty("defaultTravelMode").GetString(), "the portal and GetTravelModes agree on the default travel mode");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_SignedIn_GrantsTheNetworkAnalysisPrivilegesTheProviderSupports()
    {
        // The fixture client authenticates with the test API key, so the user block is
        // populated; the privileges must follow the provider's capabilities.
        using var response = await _fixture.Client.GetAsync("/sharing/rest/portals/self?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var user = document.RootElement.GetProperty("user");
        user.ValueKind.Should().Be(JsonValueKind.Object, "the fixture client is authenticated");
        var privileges = user.GetProperty("privileges").EnumerateArray()
            .Select(p => p.GetString()).ToArray();
        privileges.Should().Contain(
        [
            "premium:user:networkanalysis",
            "premium:user:networkanalysis:routing",
            "premium:user:networkanalysis:servicearea",
            "premium:user:networkanalysis:closestfacility",
            "premium:user:networkanalysis:origindestinationcostmatrix",
            "premium:user:networkanalysis:locationallocation",
        ]);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task UtilityTasks_AnswerUnderTheAdvertisedRoutingServiceId()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("serviceName", "asyncRoute"),
            new KeyValuePair<string, string>("toolName", "FindRoutes"),
            new KeyValuePair<string, string>("includeNetworkSourceInfo", "true"),
        ]);
        using var response = await _fixture.Client.PostAsync("/rest/services/Routing/GPServer/GetToolInfo/execute", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse("Routing is not a catalog service, but the utility tasks answer for every id");
        document.RootElement.GetProperty("results")[0].GetProperty("value").GetProperty("isPortal").GetBoolean().Should().BeTrue();

        using var travelModes = await _fixture.Client.GetAsync("/rest/services/Routing/GPServer/GetTravelModes?f=json");
        travelModes.StatusCode.Should().Be(HttpStatusCode.OK);
        using var travelModesDocument = JsonDocument.Parse(await travelModes.Content.ReadAsStringAsync());
        travelModesDocument.RootElement.GetProperty("name").GetString().Should().Be("GetTravelModes");
    }
}
