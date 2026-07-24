// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Geocoding.Features.Geocoding;
using CoreGeocodeProvider = Honua.Geocoding.Features.Geocoding.Abstractions.IGeocodeProvider;
using CoreGeocodeProviderCapabilities = Honua.Geocoding.Features.Geocoding.Domain.GeocodeProviderCapabilities;
using CoreGeocodeProviderHealth = Honua.Geocoding.Features.Geocoding.Domain.GeocodeProviderHealth;
using CoreGeocodeCandidate = Honua.Geocoding.Features.Geocoding.Domain.GeocodeCandidate;
using CoreForwardGeocodeRequest = Honua.Geocoding.Features.Geocoding.Domain.ForwardGeocodeRequest;
using CoreReverseGeocodeMatch = Honua.Geocoding.Features.Geocoding.Domain.ReverseGeocodeMatch;
using CoreReverseGeocodeRequest = Honua.Geocoding.Features.Geocoding.Domain.ReverseGeocodeRequest;
using CoreSuggestGeocodeRequest = Honua.Geocoding.Features.Geocoding.Domain.SuggestGeocodeRequest;
using CoreBatchGeocodeRequest = Honua.Geocoding.Features.Geocoding.Domain.BatchGeocodeRequest;
using CoreGeocodeSuggestion = Honua.Geocoding.Features.Geocoding.Domain.GeocodeSuggestion;
using CoreGeocodePoint = Honua.Geocoding.Features.Geocoding.Domain.GeocodePoint;
using CoreGeocodeProviderException = Honua.Geocoding.Features.Geocoding.Domain.GeocodeProviderException;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Geocoding;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Geocoding;

[Protocol(TestProtocols.Geocoding)]
public sealed class GeocodingEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    [Endpoint("GET /rest/services/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_ReturnsGeoServicesPayload()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&f=json");
        using var defaultResponse = await client.GetAsync("/rest/services/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        Assert.True(root.TryGetProperty("candidates", out var candidates));
        Assert.Equal(JsonValueKind.Array, candidates.ValueKind);
        Assert.True(candidates.GetArrayLength() > 0);

        var firstCandidate = candidates[0];
        Assert.Equal("1600 Pennsylvania Ave NW", firstCandidate.GetProperty("address").GetString());
        Assert.True(firstCandidate.TryGetProperty("location", out _));
        Assert.True(firstCandidate.TryGetProperty("attributes", out _));
    }

    // Regression (#1428): findAddressCandidates must reproject candidate locations to a
    // requested outSR (e.g. Web Mercator 3857) instead of rejecting it with 400.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithOutSr3857_ReprojectsLocations()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&outSR=3857&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        Assert.Equal(3857, root.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        var location = root.GetProperty("candidates")[0].GetProperty("location");
        var x = location.GetProperty("x").GetDouble();
        var y = location.GetProperty("y").GetDouble();

        // -77.03655, 38.89768 (WGS84) projected to Web Mercator.
        Assert.InRange(x, -8576000, -8574000);
        Assert.InRange(y, 4706000, 4708000);
    }

    // findAddressCandidates must honour outFields by projecting each candidate's
    // attribute bag down to the requested fields (case-insensitive).
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithOutFields_ProjectsAttributes()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&outFields=match_addr&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attributes = payload.RootElement.GetProperty("candidates")[0].GetProperty("attributes");

        Assert.True(attributes.TryGetProperty("Match_addr", out _));
        Assert.False(attributes.TryGetProperty("Provider", out _));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithOutFieldsWildcard_ReturnsAllAttributes()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&outFields=*&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attributes = payload.RootElement.GetProperty("candidates")[0].GetProperty("attributes");

        Assert.True(attributes.TryGetProperty("Match_addr", out _));
        Assert.True(attributes.TryGetProperty("Provider", out _));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithEmptyOutFieldsToken_ReturnsBadRequest()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&outFields=Match_addr,,Provider&f=json");

        await response.AssertGeoServicesErrorAsync(400);
    }

    // geocodeAddresses must honour outFields per result location.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task GeocodeAddresses_WithOutFields_ProjectsAttributes()
    {
        using var factory = CreateDefaultFactory(grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var addresses = Uri.EscapeDataString(
            """{"records":[{"attributes":{"OBJECTID":1,"SingleLine":"1600 Pennsylvania Ave NW"}}]}""");
        using var response = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/geocodeAddresses?addresses={addresses}&outFields=Provider&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attributes = payload.RootElement.GetProperty("locations")[0].GetProperty("attributes");

        Assert.True(attributes.TryGetProperty("Provider", out _));
        Assert.False(attributes.TryGetProperty("Match_addr", out _));
    }

    // Regression (#1442): reverseGeocode must read the INPUT location's own
    // spatialReference (here Web Mercator 3857) and use outSR only for the OUTPUT
    // geometry. A location in 3857 with outSR=3857 must reproject to the provider SRID
    // for the query and back to 3857 for the response (rather than 404).
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_WithLocationSrAndOutSr3857_ReprojectsLocation()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        // Web Mercator coordinates (roughly -77.03655, 38.89768) declared via the
        // location's own spatialReference.
        var location = Uri.EscapeDataString(
            """{"x":-8575155,"y":4707030,"spatialReference":{"wkid":3857}}""");
        using var response = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/reverseGeocode?location={location}&outSR=3857&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var locationElement = payload.RootElement.GetProperty("location");

        Assert.Equal(3857, locationElement.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.InRange(locationElement.GetProperty("x").GetDouble(), -8576000, -8574000);
        Assert.InRange(locationElement.GetProperty("y").GetDouble(), 4706000, 4708000);
    }

    // Regression (#1442): a location expressed in WGS84 (4326, the default input SR)
    // with outSR=3857 must reproject the OUTPUT to Web Mercator without 404 and without
    // misinterpreting the input location as being in outSR.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_Wgs84LocationWithOutSr3857_ReprojectsOutputToWebMercator()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        // Bare "lon,lat" defaults to the WGS84 input SR; outSR controls only the output.
        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03655,38.89768&outSR=3857&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var location = payload.RootElement.GetProperty("location");

        Assert.Equal(3857, location.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        // -77.03655, 38.89768 (WGS84) projected to Web Mercator.
        Assert.InRange(location.GetProperty("x").GetDouble(), -8576000, -8574000);
        Assert.InRange(location.GetProperty("y").GetDouble(), 4706000, 4708000);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    [Endpoint("GET /rest/services/GeocodeServer")]
    public async Task GeocodeServerMetadata_ExposesProviderCapabilities()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");
        using var defaultResponse = await client.GetAsync("/rest/services/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capabilities = payload.RootElement.GetProperty("capabilities").GetString();
        Assert.Equal("Geocode,ReverseGeocode,Suggest", capabilities);

        // candidateFields advertises the attributes every candidate emits; categories
        // advertises the address/place families candidates are classified into, now that
        // category filtering is supported on the shared interface (#1867).
        var candidateFields = payload.RootElement.GetProperty("candidateFields");
        Assert.Equal(JsonValueKind.Array, candidateFields.ValueKind);
        Assert.Contains(
            candidateFields.EnumerateArray(),
            field => field.GetProperty("name").GetString() == "Match_addr");
        Assert.Equal(JsonValueKind.Array, payload.RootElement.GetProperty("categories").ValueKind);
        Assert.True(payload.RootElement.GetProperty("categories").GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer")]
    [Endpoint("POST /rest/services/GeocodeServer")]
    public async Task GeocodeServerMetadata_Post_ReturnsSamePayloadAsGet()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var getResponse = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");
        using var postContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("f", "json"),
        });
        using var postResponse = await client.PostAsync("/rest/services/World/GeocodeServer", postContent);

        using var aliasPostContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("f", "json"),
        });
        using var aliasPostResponse = await client.PostAsync("/rest/services/GeocodeServer", aliasPostContent);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, aliasPostResponse.StatusCode);

        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();
        Assert.Equal(getBody, postBody);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/suggest")]
    [Endpoint("GET /rest/services/GeocodeServer/suggest")]
    public async Task Suggest_ReturnsBadRequest_WhenExplicitProviderDoesNotSupportSuggest()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: false,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=hon&provider=fake&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not supported", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_ReturnsBadRequest_WhenExplicitProviderDoesNotSupportBatch()
    {
        using var factory = CreateDefaultFactory(grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"test address"}}]""";
        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&provider=fake&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not supported", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    [Endpoint("GET /rest/services/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_ReturnsGeoServicesPayload()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03655,38.89768&f=json");
        using var defaultResponse = await client.GetAsync("/rest/services/GeocodeServer/reverseGeocode?location=-77.03655,38.89768&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal("1600 Pennsylvania Ave NW", root.GetProperty("address").GetProperty("Match_addr").GetString());
        Assert.Equal(-77.03655, root.GetProperty("location").GetProperty("x").GetDouble(), precision: 5);
        Assert.Equal(38.89768, root.GetProperty("location").GetProperty("y").GetDouble(), precision: 5);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    public async Task Suggest_WhenProviderSupports_ReturnsSuggestions()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=hon&f=json");
        using var defaultResponse = await client.GetAsync("/rest/services/GeocodeServer/suggest?text=hon&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.True(root.TryGetProperty("suggestions", out var suggestions));
        Assert.Equal(JsonValueKind.Array, suggestions.ValueKind);
        Assert.True(suggestions.GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/suggest")]
    public async Task Suggest_Post_WhenProviderSupports_ReturnsSuggestions()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = "hon",
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/suggest", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.True(root.TryGetProperty("suggestions", out var suggestions));
        Assert.Equal(JsonValueKind.Array, suggestions.ValueKind);
        Assert.True(suggestions.GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    [Endpoint("GET /rest/services/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_HappyPath_ReturnsLocationsArray()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}},{"attributes":{"SingleLine":"350 Fifth Avenue, New York"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");
        using var defaultResponse = await client.GetAsync($"/rest/services/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        Assert.True(root.TryGetProperty("locations", out var locations));
        Assert.Equal(JsonValueKind.Array, locations.ValueKind);
        Assert.Equal(2, locations.GetArrayLength());

        var firstLocation = locations[0];
        Assert.Equal("1600 Pennsylvania Ave NW", firstLocation.GetProperty("address").GetString());
        Assert.True(firstLocation.TryGetProperty("location", out _));
        Assert.True(firstLocation.TryGetProperty("score", out _));
        Assert.True(firstLocation.TryGetProperty("attributes", out _));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_Post_ReturnsLocationsArray()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["records"] = records,
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/geocodeAddresses", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.TryGetProperty("locations", out var locations));
        Assert.Equal(1, locations.GetArrayLength());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_Post_WithEsriAddressesParameter_ReturnsLocationsArray()
    {
        // The ArcGIS API for Python batch_geocode and ArcGIS Pro send the batch payload under
        // the "addresses" parameter (addresses={"records":[{"attributes":{...}}]}), not "records".
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var addresses = """{"records":[{"attributes":{"OBJECTID":1,"SingleLine":"1600 Pennsylvania Ave NW"}},{"attributes":{"OBJECTID":2,"SingleLine":"350 Fifth Avenue, New York"}}]}""";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["addresses"] = addresses,
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/geocodeAddresses", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.True(root.TryGetProperty("locations", out var locations));
        Assert.Equal(JsonValueKind.Array, locations.ValueKind);
        Assert.Equal(2, locations.GetArrayLength());
        Assert.True(locations[0].TryGetProperty("location", out _));
        Assert.True(locations[0].TryGetProperty("score", out _));
    }

    // Regression (#1263): the ArcGIS Maps SDK for JavaScript locator.addressesToLocations
    // (and ArcGIS Pro) serialize a configured output spatial reference as the canonical
    // Esri JSON object, e.g. outSR={"wkid":4326}, not a bare WKID integer. The batch
    // geocodeAddresses op must accept that JSON spatial-reference shape rather than
    // rejecting it with "Invalid outSR parameter." (HTTP 400).
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_Post_WithEsriJsonOutSr_AcceptsSpatialReferenceObject()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var addresses = """{"records":[{"attributes":{"OBJECTID":1,"SingleLine":"1600 Pennsylvania Ave NW"}}]}""";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["addresses"] = addresses,
            // Canonical Esri spatial-reference JSON object, as the JS SDK / Pro send it.
            ["outSR"] = """{"wkid":4326}""",
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/geocodeAddresses", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.True(root.TryGetProperty("spatialReference", out var spatialReference));
        Assert.Equal(4326, spatialReference.GetProperty("wkid").GetInt32());
        Assert.True(root.TryGetProperty("locations", out var locations));
        Assert.Equal(1, locations.GetArrayLength());
    }

    // Regression (#1263): findAddressCandidates must likewise accept the Esri JSON
    // spatial-reference object for outSR (latestWkid form), reprojecting candidate
    // locations to that WKID rather than rejecting the request with HTTP 400.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithEsriJsonOutSr_AcceptsSpatialReferenceObject()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        var outSr = Uri.EscapeDataString("""{"latestWkid":3857}""");
        using var response = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=1600+Pennsylvania+Ave+NW&outSR={outSr}&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal(3857, root.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_Post_EmitsJsSdkAddressesToLocationsResponseShape()
    {
        // The ArcGIS Maps SDK for JavaScript locator.addressesToLocations switches from GET
        // to POST once the addresses payload makes the URL too long. Its response parser reads
        // { locations, spatialReference } from the response ROOT and maps each entry via
        // AddressCandidate.fromJSON, which requires address/location/score per location and a
        // root spatialReference applied to each location. This locks that shape in for the POST path.
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var addresses = """{"records":[{"attributes":{"OBJECTID":1,"SingleLine":"1600 Pennsylvania Ave NW"}}]}""";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["addresses"] = addresses,
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/geocodeAddresses", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        // Root-level keys the JS SDK destructures: { locations, spatialReference }.
        Assert.True(root.TryGetProperty("spatialReference", out var spatialReference));
        Assert.True(spatialReference.TryGetProperty("wkid", out _));
        Assert.True(root.TryGetProperty("locations", out var locations));
        Assert.Equal(JsonValueKind.Array, locations.ValueKind);
        Assert.Equal(1, locations.GetArrayLength());

        // Per-location keys AddressCandidate.fromJSON consumes.
        var first = locations[0];
        Assert.True(first.TryGetProperty("address", out _));
        Assert.True(first.TryGetProperty("score", out _));
        Assert.True(first.TryGetProperty("location", out var location));
        Assert.True(location.TryGetProperty("x", out _));
        Assert.True(location.TryGetProperty("y", out _));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_AdvertisesSupportsSuggest_WhenProviderSupports()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        Assert.Contains("Suggest", root.GetProperty("capabilities").GetString(), StringComparison.Ordinal);
        var locatorProperties = root.GetProperty("locatorProperties");
        Assert.Equal("true", locatorProperties.GetProperty("SupportsSuggest").GetString());
        Assert.Equal("true", locatorProperties.GetProperty("SupportsBatch").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_MissingRecords_Returns400()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/geocodeAddresses?f=json");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_MalformedRecordsJson_Returns400()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/geocodeAddresses?records=not-valid-json&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid JSON", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_ExceedsMaxBatchSize_Returns400()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)
        { MaxBatchSize = 2 }), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"addr1"}},{"attributes":{"SingleLine":"addr2"}},{"attributes":{"SingleLine":"addr3"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&provider=fake&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("exceeds the maximum", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_InvalidRecordInBatch_Returns400WithIndex()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)), grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"valid address"}},{"attributes":{}},{"attributes":{"SingleLine":"another valid"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("index 1", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithInvalidLocatorName_Returns404()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/NonexistentLocator/GeocodeServer/findAddressCandidates?singleLine=test&f=json");

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_Post_ReturnsGeoServicesPayload()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["singleLine"] = "1600 Pennsylvania Ave NW",
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/findAddressCandidates", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.TryGetProperty("candidates", out var candidates));
        Assert.True(candidates.GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_Post_ReturnsGeoServicesPayload()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["location"] = "-77.03655,38.89768",
            ["f"] = "json"
        });

        using var response = await client.PostAsync("/rest/services/World/GeocodeServer/reverseGeocode", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.TryGetProperty("address", out _));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_PjsonFormat_ReturnsJson()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&f=pjson");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_InvalidFormat_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&f=xml");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_WhenGeocodingDisabled_Returns404()
    {
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "false"
                });
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_MissingLocation_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        // Missing location parameter
        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/reverseGeocode?f=json");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task ForwardGeocode_ReturnsBadRequest_WhenExplicitProviderNotFound()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&provider=nonexistent&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not found", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_ReturnsBadRequest_WhenExplicitProviderNotFound()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03,38.89&provider=nonexistent&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not found", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task ForwardGeocode_PrimaryFails_FallsBackToSecondary()
    {
        using var factory = CreateFailoverFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test+address&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var candidates = payload.RootElement.GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() > 0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_PrimaryFails_FallsBackToSecondary()
    {
        using var factory = CreateFailoverFactory(grantEnterpriseForBatch: true);
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var locations = payload.RootElement.GetProperty("locations");
        Assert.Equal(1, locations.GetArrayLength());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task ForwardGeocode_AllProvidersFail_ReturnsError()
    {
        var failingProvider = new FailingGeocodeProvider("only-failing");
        // Override all auto-registered providers to ensure all providers fail
        var nominatimOverride = new FailingGeocodeProvider("nominatim");
        var azureOverride = new FailingGeocodeProvider("azuremaps");
        var amazonOverride = new FailingGeocodeProvider("amazonlocation");
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "true",
                    ["Geocoding:DefaultProvider"] = failingProvider.Name,
                    ["Geocoding:LocatorName"] = "World",
                    ["Geocoding:DefaultSpatialReferenceWkid"] = "4326",
                    ["Geocoding:EnableFailover"] = "true",
                    ["Geocoding:MaxFailoverAttempts"] = "10"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddGeocodeProvider(failingProvider.Name, _ => failingProvider, ServiceLifetime.Singleton);
                services.AddGeocodeProvider(nominatimOverride.Name, _ => nominatimOverride, ServiceLifetime.Singleton);
                services.AddGeocodeProvider(azureOverride.Name, _ => azureOverride, ServiceLifetime.Singleton);
                services.AddGeocodeProvider(amazonOverride.Name, _ => amazonOverride, ServiceLifetime.Singleton);
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&f=json");

        // The coordinator catches the exception and returns a failure result; handler maps to error
        await response.AssertGeoServicesErrorAsync(400, 500);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithSearchExtent_PassesSearchBoundsToProvider()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        // GET path accepts the Esri JSON envelope form of searchExtent.
        var envelope = """{"xmin":-90.0,"ymin":39.0,"xmax":-89.0,"ymax":40.0,"spatialReference":{"wkid":4326}}""";
        using var getResponse = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=Springfield&searchExtent={Uri.EscapeDataString(envelope)}&f=json");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.NotNull(fakeProvider.LastForwardRequest);
        Assert.NotNull(fakeProvider.LastForwardRequest!.SearchBounds);
        Assert.Equal(-90.0, fakeProvider.LastForwardRequest.SearchBounds!.XMin, precision: 5);
        Assert.Equal(39.0, fakeProvider.LastForwardRequest.SearchBounds.YMin, precision: 5);
        Assert.Equal(-89.0, fakeProvider.LastForwardRequest.SearchBounds.XMax, precision: 5);
        Assert.Equal(40.0, fakeProvider.LastForwardRequest.SearchBounds.YMax, precision: 5);

        // POST path accepts the comma-delimited "xmin,ymin,xmax,ymax" form.
        using var postContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["singleLine"] = "Springfield",
            ["searchExtent"] = "-100.0,30.0,-99.0,31.0",
            ["f"] = "json"
        });
        using var postResponse = await client.PostAsync("/rest/services/World/GeocodeServer/findAddressCandidates", postContent);

        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        Assert.NotNull(fakeProvider.LastForwardRequest!.SearchBounds);
        Assert.Equal(-100.0, fakeProvider.LastForwardRequest.SearchBounds!.XMin, precision: 5);
        Assert.Equal(31.0, fakeProvider.LastForwardRequest.SearchBounds.YMax, precision: 5);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithInvalidSearchExtent_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&searchExtent=not-an-extent&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("searchExtent", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_WithLangCode_PassesLanguageCodeToProvider()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/reverseGeocode?location=2.3522,48.8566&langCode=fr&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(fakeProvider.LastReverseRequest);
        Assert.Equal("fr", fakeProvider.LastReverseRequest!.LanguageCode);
    }

    // reverseGeocode `distance` is a provider-capability-dependent search radius
    // (meters). The adapter must pass it through to the canonical request so a
    // backing provider that honors it (e.g. Azure Maps `radius`) can apply it.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_WithDistance_PassesDistanceMetersToProvider()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03655,38.89768&distance=250&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(fakeProvider.LastReverseRequest);
        Assert.Equal(250.0, fakeProvider.LastReverseRequest!.DistanceMeters);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_WithInvalidDistance_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03655,38.89768&distance=0&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("distance", body, StringComparison.OrdinalIgnoreCase);
    }

    // reverseGeocode `featureTypes` is a provider-capability-dependent type filter.
    // The adapter must pass the parsed tokens through to the canonical request so a
    // backing provider that honors it (e.g. Azure Maps `entityType`) can apply it.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/reverseGeocode")]
    public async Task ReverseGeocode_WithFeatureTypes_PassesFeatureTypesToProvider()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/reverseGeocode?location=-77.03655,38.89768&featureTypes=PointAddress,StreetAddress&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(fakeProvider.LastReverseRequest);
        Assert.NotNull(fakeProvider.LastReverseRequest!.FeatureTypes);
        Assert.Equal(["PointAddress", "StreetAddress"], fakeProvider.LastReverseRequest.FeatureTypes!);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/suggest")]
    public async Task Suggest_WithSearchExtentAndLocation_PassesBoundsAndBiasToProvider()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        var envelope = """{"xmin":-1.0,"ymin":50.0,"xmax":1.0,"ymax":52.0}""";
        using var response = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/suggest?text=Vic&location=-0.12,51.5&searchExtent={Uri.EscapeDataString(envelope)}&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(fakeProvider.LastSuggestRequest);
        Assert.NotNull(fakeProvider.LastSuggestRequest!.SearchBounds);
        Assert.Equal(-1.0, fakeProvider.LastSuggestRequest.SearchBounds!.XMin, precision: 5);
        Assert.Equal(52.0, fakeProvider.LastSuggestRequest.SearchBounds.YMax, precision: 5);
        Assert.NotNull(fakeProvider.LastSuggestRequest.BiasLocation);
        Assert.Equal(-0.12, fakeProvider.LastSuggestRequest.BiasLocation!.X, precision: 5);
        Assert.Equal(51.5, fakeProvider.LastSuggestRequest.BiasLocation.Y, precision: 5);
    }

    // #1867: magicKey suggest->candidate round-trip. The handler re-mints each suggestion's
    // magicKey as a self-issued, signed, opaque token; findAddressCandidates resolves it
    // deterministically. The provider's own magicKeys (fake-suggest-*) are never surfaced.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    public async Task Suggest_EmitsSelfIssuedMagicKey_NotProviderInternalId()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=1600&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var suggestions = payload.RootElement.GetProperty("suggestions");
        Assert.True(suggestions.GetArrayLength() > 0);

        var magicKey = suggestions[0].GetProperty("magicKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(magicKey));
        // The opaque token is self-issued, not the provider-internal id.
        Assert.DoesNotContain("fake-suggest", magicKey, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    public async Task Suggest_EmitsDeterministicMagicKey_AcrossRepeatedCalls()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=1600&f=json");
        using var second = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=1600&f=json");

        using var firstPayload = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondPayload = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        var firstKey = firstPayload.RootElement.GetProperty("suggestions")[0].GetProperty("magicKey").GetString();
        var secondKey = secondPayload.RootElement.GetProperty("suggestions")[0].GetProperty("magicKey").GetString();

        Assert.Equal(firstKey, secondKey);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task SuggestThenFindAddressCandidates_WithMagicKey_RoundTripsDeterministically()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        // 1) suggest issues an opaque magicKey for the picked suggestion.
        using var suggestResponse = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=1600&f=json");
        Assert.Equal(HttpStatusCode.OK, suggestResponse.StatusCode);
        using var suggestPayload = JsonDocument.Parse(await suggestResponse.Content.ReadAsStringAsync());
        var suggestion = suggestPayload.RootElement.GetProperty("suggestions")[0];
        var magicKey = suggestion.GetProperty("magicKey").GetString()!;
        var suggestionText = suggestion.GetProperty("text").GetString();

        // 2) findAddressCandidates resolves the magicKey directly (singleLine is the SDK-required echo).
        using var candidatesResponse = await client.GetAsync(
            $"/rest/services/World/GeocodeServer/findAddressCandidates?singleLine={Uri.EscapeDataString(suggestionText!)}&magicKey={Uri.EscapeDataString(magicKey)}&f=json");

        Assert.Equal(HttpStatusCode.OK, candidatesResponse.StatusCode);
        using var candidatesPayload = JsonDocument.Parse(await candidatesResponse.Content.ReadAsStringAsync());
        var candidates = candidatesPayload.RootElement.GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() > 0);

        // The forward query used the magicKey's encoded suggestion text, not free input.
        Assert.NotNull(fakeProvider.LastForwardRequest);
        Assert.Equal(suggestionText, fakeProvider.LastForwardRequest!.Query);
        Assert.Equal(magicKey, fakeProvider.LastForwardRequest.MagicKey);

        // The first suggestion was PointAddress; its encoded category narrows resolution to the
        // address-typed candidate only (the POI candidate is filtered out).
        Assert.Equal(1, candidates.GetArrayLength());
        Assert.Equal("1600 Pennsylvania Ave NW", candidates[0].GetProperty("address").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithInvalidMagicKey_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&magicKey=tampered.token&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("magicKey", body, StringComparison.OrdinalIgnoreCase);
    }

    // #1867: forward category filtering narrows candidates by the provider-supplied address type.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithCategory_NarrowsCandidates()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        // No filter: both the PointAddress and POI candidates are returned.
        using var unfiltered = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=Pennsylvania&f=json");
        using var unfilteredPayload = JsonDocument.Parse(await unfiltered.Content.ReadAsStringAsync());
        Assert.Equal(2, unfilteredPayload.RootElement.GetProperty("candidates").GetArrayLength());

        // category=POI keeps only the POI candidate.
        using var filtered = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=Pennsylvania&category=POI&f=json");

        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        using var filteredPayload = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        var candidates = filteredPayload.RootElement.GetProperty("candidates");
        Assert.Equal(1, candidates.GetArrayLength());
        Assert.Equal("White House Visitor Center", candidates[0].GetProperty("address").GetString());

        // The handler forwards the requested category on the canonical request.
        Assert.Equal("POI", GetLastForwardCategory(factory));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    public async Task Suggest_WithCategory_NarrowsSuggestions()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/suggest?text=hon&category=POI&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var suggestions = payload.RootElement.GetProperty("suggestions");
        Assert.Equal(1, suggestions.GetArrayLength());
        Assert.Equal("Honua HQ", suggestions[0].GetProperty("text").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_AdvertisesSupportedCategories()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var categories = payload.RootElement.GetProperty("categories");
        Assert.Equal(JsonValueKind.Array, categories.ValueKind);
        Assert.True(categories.GetArrayLength() > 0);
        var values = categories.EnumerateArray().Select(static c => c.GetString()).ToArray();
        Assert.Contains("POI", values);
        Assert.Contains("Address", values);
    }

    // #2147: SuggestedBatchSize is derived from the ACTIVE provider's MaxBatchSize, not a constant.
    // A provider with a non-default batch size advertises that exact value.
    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_SuggestedBatchSize_ReflectsProviderMaxBatchSize()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)
        { MaxBatchSize = 37 }));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var locatorProperties = payload.RootElement.GetProperty("locatorProperties");

        Assert.Equal("37", locatorProperties.GetProperty("SuggestedBatchSize").GetString());
        Assert.Equal("true", locatorProperties.GetProperty("SupportsBatch").GetString());
    }

    // #2147: SuggestedBatchSize differs across providers with differing MaxBatchSize, proving the
    // metadata is per-provider rather than a hard-coded 100.
    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_SuggestedBatchSize_VariesByProvider()
    {
        using var smallFactory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)
        { MaxBatchSize = 10 }));
        using var smallClient = smallFactory.CreateClient();

        using var largeFactory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true)
        { MaxBatchSize = 250 }));
        using var largeClient = largeFactory.CreateClient();

        using var smallResponse = await smallClient.GetAsync("/rest/services/World/GeocodeServer?f=json");
        using var largeResponse = await largeClient.GetAsync("/rest/services/World/GeocodeServer?f=json");

        using var smallPayload = JsonDocument.Parse(await smallResponse.Content.ReadAsStringAsync());
        using var largePayload = JsonDocument.Parse(await largeResponse.Content.ReadAsStringAsync());

        Assert.Equal(
            "10",
            smallPayload.RootElement.GetProperty("locatorProperties").GetProperty("SuggestedBatchSize").GetString());
        Assert.Equal(
            "250",
            largePayload.RootElement.GetProperty("locatorProperties").GetProperty("SuggestedBatchSize").GetString());
    }

    // #2147: when the active provider does not support batch, SuggestedBatchSize is ABSENT rather
    // than falsely advertised, keeping clients from attempting an unsupported batch call.
    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer")]
    public async Task GeocodeServerMetadata_SuggestedBatchSize_AbsentWhenBatchUnsupported()
    {
        using var factory = CreateFactory(new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var locatorProperties = payload.RootElement.GetProperty("locatorProperties");

        Assert.False(locatorProperties.TryGetProperty("SuggestedBatchSize", out _));
        Assert.Equal("false", locatorProperties.GetProperty("SupportsBatch").GetString());
        Assert.DoesNotContain("BatchGeocode", payload.RootElement.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);
    }

    // #2148: findAddressCandidates accepts location/distance and passes them to the canonical
    // forward request so a proximity-aware provider can bias results.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithLocationAndDistance_PassesBiasToProvider()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=Pennsylvania&location=-77.0,38.9&distance=500&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(fakeProvider.LastForwardRequest);
        Assert.NotNull(fakeProvider.LastForwardRequest!.BiasLocation);
        Assert.Equal(-77.0, fakeProvider.LastForwardRequest.BiasLocation!.X, precision: 5);
        Assert.Equal(38.9, fakeProvider.LastForwardRequest.BiasLocation.Y, precision: 5);
        Assert.Equal(500.0, fakeProvider.LastForwardRequest.BiasDistanceMeters);
    }

    // #2148: a proximity bias re-ranks equally-matching candidates so the nearest is returned first.
    // The fixture's POI candidate (-77.03361, 38.89466) is nearer the bias point than the address
    // candidate (-77.03655, 38.89768), so biasing flips the default ordering deterministically.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithProximityBias_RanksNearbyCandidateFirst()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        // Unbiased: the address candidate (higher score) is first.
        using var unbiased = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=Pennsylvania&f=json");
        using var unbiasedPayload = JsonDocument.Parse(await unbiased.Content.ReadAsStringAsync());
        Assert.Equal(
            "1600 Pennsylvania Ave NW",
            unbiasedPayload.RootElement.GetProperty("candidates")[0].GetProperty("address").GetString());

        // Biased toward the POI's coordinates: the nearer POI candidate ranks first.
        using var biased = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=Pennsylvania&location=-77.03361,38.89466&f=json");
        using var biasedPayload = JsonDocument.Parse(await biased.Content.ReadAsStringAsync());
        Assert.Equal(
            "White House Visitor Center",
            biasedPayload.RootElement.GetProperty("candidates")[0].GetProperty("address").GetString());
    }

    // #2148: a provider WITHOUT proximity support ignores the bias gracefully (no error, default
    // ordering preserved). The bias still reaches the canonical request; the provider chooses to
    // ignore it because SupportsBiasing is false.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithBias_IgnoredGracefully_WhenProviderUnsupported()
    {
        var fakeProvider = new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: false,
            SupportsStructuredInput: false,
            SupportsBiasing: false));
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=Pennsylvania&location=-77.03361,38.89466&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // No error and default ordering is preserved (bias gracefully ignored by the provider).
        Assert.Equal(
            "1600 Pennsylvania Ave NW",
            payload.RootElement.GetProperty("candidates")[0].GetProperty("address").GetString());
    }

    // #2148: an invalid bias location is rejected with a clear error.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithInvalidLocation_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&location=not-a-point&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("location", body, StringComparison.OrdinalIgnoreCase);
    }

    // #2148: an invalid bias distance is rejected with a clear error.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_WithInvalidDistance_Returns400()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&location=-77.0,38.9&distance=-5&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("distance", body, StringComparison.OrdinalIgnoreCase);
    }

    // #2148: suggest passes a bias distance through alongside the existing bias location.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/suggest")]
    public async Task Suggest_WithLocationAndDistance_PassesBiasDistanceToProvider()
    {
        var fakeProvider = new FakeGeocodeProvider(DefaultCapabilities);
        using var factory = CreateFactory(fakeProvider);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/services/World/GeocodeServer/suggest?text=Vic&location=-0.12,51.5&distance=1000&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(fakeProvider.LastSuggestRequest);
        Assert.NotNull(fakeProvider.LastSuggestRequest!.BiasLocation);
        Assert.Equal(1000.0, fakeProvider.LastSuggestRequest.BiasDistanceMeters);
    }

    private static string? GetLastForwardCategory(WebApplicationFactory<Program> factory)
    {
        var provider = (FakeGeocodeProvider)factory.Services
            .GetServices<CoreGeocodeProvider>()
            .First(static p => p.Name == "fake");
        return provider.LastForwardRequest?.CategoryFilter;
    }

    private static readonly CoreGeocodeProviderCapabilities DefaultCapabilities = new(
        SupportsSuggest: true,
        SupportsBatch: false,
        SupportsStructuredInput: false,
        SupportsBiasing: true);

    private static WebApplicationFactory<Program> CreateDefaultFactory(bool grantEnterpriseForBatch = false)
        => CreateFactory(new FakeGeocodeProvider(DefaultCapabilities), grantEnterpriseForBatch);

    // #2981: geocodeAddresses (batch) now requires the Enterprise geocoding.batch entitlement
    // (GeocodingEntitlementGateTests proves the gate itself in both directions). Every batch test
    // here exercises the handler's own logic downstream of the gate, so it opts in via
    // grantEnterpriseForBatch — forward/reverse/suggest tests leave it off since those stayed
    // Community and need no license at all.
    private static WebApplicationFactory<Program> CreateFactory(FakeGeocodeProvider fakeProvider, bool grantEnterpriseForBatch = false)
    {
        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "true",
                    ["Geocoding:DefaultProvider"] = fakeProvider.Name,
                    ["Geocoding:LocatorName"] = "World",
                    ["Geocoding:DefaultSpatialReferenceWkid"] = "4326"
                });
            });

            // Licensing:DevGrantEdition is read by AddHonuaLicensing while the minimal-hosting-model
            // WebApplicationBuilder is still being configured, before a ConfigureAppConfiguration
            // callback added here would apply. UseSetting (mirrors the #2978
            // IdentityEntitlementGateTests pattern) pushes it in early enough to be honored.
            if (grantEnterpriseForBatch)
            {
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
            }

            builder.ConfigureServices(services =>
            {
                services.AddGeocodeProvider(fakeProvider.Name, _ => fakeProvider, ServiceLifetime.Singleton);
                // The lightweight geocoding test harness does not wire the provider
                // infrastructure, so register a WGS84<->Web Mercator transform for the
                // outSR reprojection path (#1428).
                services.RemoveAll<ICoordinateTransformService>();
                services.AddSingleton<ICoordinateTransformService, WebMercatorCoordinateTransformService>();
            });
        });
    }

    private static WebApplicationFactory<Program> CreateFailoverFactory(bool grantEnterpriseForBatch = false)
    {
        var failingProvider = new FailingGeocodeProvider("failing-primary");
        var workingProvider = new FakeGeocodeProvider(new CoreGeocodeProviderCapabilities(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true));

        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Geocoding:Enabled"] = "true",
                    ["Geocoding:DefaultProvider"] = failingProvider.Name,
                    ["Geocoding:LocatorName"] = "World",
                    ["Geocoding:DefaultSpatialReferenceWkid"] = "4326",
                    ["Geocoding:EnableFailover"] = "true",
                    // High enough to exhaust all auto-registered providers before reaching fake
                    ["Geocoding:MaxFailoverAttempts"] = "10"
                });
            });

            // See CreateFactory above: Licensing:DevGrantEdition must go through UseSetting, not
            // ConfigureAppConfiguration, to be honored by AddHonuaLicensing.
            if (grantEnterpriseForBatch)
            {
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
            }

            builder.ConfigureServices(services =>
            {
                services.AddGeocodeProvider(failingProvider.Name, _ => failingProvider, ServiceLifetime.Singleton);
                services.AddGeocodeProvider(workingProvider.Name, _ => workingProvider, ServiceLifetime.Singleton);
            });
        });
    }

    private sealed class FakeGeocodeProvider(CoreGeocodeProviderCapabilities capabilities) : CoreGeocodeProvider
    {
        public string Name => "fake";

        public CoreGeocodeProviderCapabilities Capabilities => capabilities;

        // Captured request inputs so adapter parameter wiring (searchExtent/location/langCode)
        // can be asserted without depending on a live upstream geocoder.
        public CoreForwardGeocodeRequest? LastForwardRequest { get; private set; }

        public CoreReverseGeocodeRequest? LastReverseRequest { get; private set; }

        public CoreSuggestGeocodeRequest? LastSuggestRequest { get; private set; }

        public Task<IReadOnlyList<CoreGeocodeCandidate>> ForwardGeocodeAsync(CoreForwardGeocodeRequest request, CancellationToken cancellationToken)
        {
            LastForwardRequest = request;

            // Emit a mix of address-typed candidates so category filtering has something to narrow.
            // The handler filters by AddressType on the shared interface, so the provider always
            // returns the full set and lets the adapter apply the requested category.
            var addressCandidate = new CoreGeocodeCandidate(
                Address: "1600 Pennsylvania Ave NW",
                X: -77.03655,
                Y: 38.89768,
                Score: 99.1,
                Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Provider"] = Name,
                    ["Match_addr"] = "1600 Pennsylvania Ave NW"
                },
                ProviderId: "fake-1")
            {
                AddressType = "PointAddress"
            };

            var poiCandidate = new CoreGeocodeCandidate(
                Address: "White House Visitor Center",
                X: -77.03361,
                Y: 38.89466,
                Score: 90.0,
                Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Provider"] = Name,
                    ["Match_addr"] = "White House Visitor Center"
                },
                ProviderId: "fake-2")
            {
                AddressType = "POI"
            };

            IReadOnlyList<CoreGeocodeCandidate> ordered = [addressCandidate, poiCandidate];

            // Proximity bias (#2148): when the provider advertises biasing and a bias location is
            // supplied, rank candidates nearest the bias point first. This proves the location/
            // distance bias flows through to the provider and changes ordering for a deterministic
            // fixture. A provider without biasing (SupportsBiasing=false) leaves ordering untouched.
            if (capabilities.SupportsBiasing && request.BiasLocation is not null)
            {
                ordered = ordered
                    .OrderBy(c => SquaredDistance(c, request.BiasLocation))
                    .ToArray();
            }

            return Task.FromResult(ordered);

            static double SquaredDistance(CoreGeocodeCandidate candidate, CoreGeocodePoint bias)
            {
                var dx = candidate.X - bias.X;
                var dy = candidate.Y - bias.Y;
                return (dx * dx) + (dy * dy);
            }
        }

        public Task<CoreReverseGeocodeMatch?> ReverseGeocodeAsync(CoreReverseGeocodeRequest request, CancellationToken cancellationToken)
        {
            LastReverseRequest = request;

            var match = new CoreReverseGeocodeMatch(
                Address: "1600 Pennsylvania Ave NW",
                X: request.X,
                Y: request.Y,
                Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Provider"] = Name,
                    ["Match_addr"] = "1600 Pennsylvania Ave NW"
                },
                ProviderId: "fake-rev-1");

            return Task.FromResult<CoreReverseGeocodeMatch?>(match);
        }

        public Task<IReadOnlyList<CoreGeocodeSuggestion>> SuggestAsync(CoreSuggestGeocodeRequest request, CancellationToken cancellationToken)
        {
            LastSuggestRequest = request;

            // Provider-internal magicKeys (fake-suggest-*) are intentionally not resolvable; the
            // handler re-mints a self-issued token. Distinct categories let category filtering and
            // the magicKey round-trip be asserted end-to-end.
            var addressSuggestion = new CoreGeocodeSuggestion("1600 Pennsylvania Ave NW", "fake-suggest-1", false)
            {
                Category = "PointAddress"
            };

            var poiSuggestion = new CoreGeocodeSuggestion("Honua HQ", "fake-suggest-2", false)
            {
                Category = "POI"
            };

            return Task.FromResult<IReadOnlyList<CoreGeocodeSuggestion>>([addressSuggestion, poiSuggestion]);
        }

        public Task<IReadOnlyList<CoreGeocodeCandidate>> BatchGeocodeAsync(CoreBatchGeocodeRequest request, CancellationToken cancellationToken)
        {
            var results = new List<CoreGeocodeCandidate>();
            for (int i = 0; i < request.Queries.Count; i++)
            {
                results.Add(new CoreGeocodeCandidate(
                    Address: request.Queries[i],
                    X: -77.03655 + i,
                    Y: 38.89768 + i,
                    Score: 95.0,
                    Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["Provider"] = Name,
                        ["Match_addr"] = request.Queries[i]
                    },
                    ProviderId: $"fake-batch-{i}"));
            }

            return Task.FromResult<IReadOnlyList<CoreGeocodeCandidate>>(results);
        }

        public Task<CoreGeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CoreGeocodeProviderHealth(Name, true));
    }

    private sealed class FailingGeocodeProvider(string name) : CoreGeocodeProvider
    {
        private static readonly CoreGeocodeProviderCapabilities FailingCapabilities = new(
            SupportsSuggest: true,
            SupportsBatch: true,
            SupportsStructuredInput: false,
            SupportsBiasing: true);

        public string Name => name;

        public CoreGeocodeProviderCapabilities Capabilities => FailingCapabilities;

        public Task<IReadOnlyList<CoreGeocodeCandidate>> ForwardGeocodeAsync(CoreForwardGeocodeRequest request, CancellationToken cancellationToken)
            => throw new CoreGeocodeProviderException("Provider unavailable");

        public Task<CoreReverseGeocodeMatch?> ReverseGeocodeAsync(CoreReverseGeocodeRequest request, CancellationToken cancellationToken)
            => throw new CoreGeocodeProviderException("Provider unavailable");

        public Task<IReadOnlyList<CoreGeocodeSuggestion>> SuggestAsync(CoreSuggestGeocodeRequest request, CancellationToken cancellationToken)
            => throw new CoreGeocodeProviderException("Provider unavailable");

        public Task<IReadOnlyList<CoreGeocodeCandidate>> BatchGeocodeAsync(CoreBatchGeocodeRequest request, CancellationToken cancellationToken)
            => throw new CoreGeocodeProviderException("Provider unavailable");

        public Task<CoreGeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CoreGeocodeProviderHealth(Name, false));
    }

    private sealed class WebMercatorCoordinateTransformService : ICoordinateTransformService
    {
        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX, double minY, double maxX, double maxY, int fromSrid, int toSrid, CancellationToken cancellationToken = default)
        {
            var (x0, y0) = Transform(minX, minY, fromSrid, toSrid);
            var (x1, y1) = Transform(maxX, maxY, fromSrid, toSrid);
            return ValueTask.FromResult<(double MinX, double MinY, double MaxX, double MaxY)?>((x0, y0, x1, y1));
        }

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x, double y, int fromSrid, int toSrid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double X, double Y)?>(Transform(x, y, fromSrid, toSrid));

        private static (double X, double Y) Transform(double x, double y, int fromSrid, int toSrid)
        {
            if (fromSrid == toSrid)
            {
                return (x, y);
            }

            if (fromSrid == 4326 && toSrid == 3857)
            {
                return WebMercatorMath.LonLatToWebMercator(x, y);
            }

            if (fromSrid == 3857 && toSrid == 4326)
            {
                var (lon, lat) = WebMercatorMath.WebMercatorToLonLat(x, y);
                return (lon, lat);
            }

            throw new NotSupportedException($"Unsupported transform {fromSrid}->{toSrid}.");
        }
    }
}
