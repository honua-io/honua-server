// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Geocoding;
using CoreGeocodeProvider = Honua.Core.Features.Geocoding.Abstractions.IGeocodeProvider;
using CoreGeocodeProviderCapabilities = Honua.Core.Features.Geocoding.Domain.GeocodeProviderCapabilities;
using CoreGeocodeProviderHealth = Honua.Core.Features.Geocoding.Domain.GeocodeProviderHealth;
using CoreGeocodeCandidate = Honua.Core.Features.Geocoding.Domain.GeocodeCandidate;
using CoreForwardGeocodeRequest = Honua.Core.Features.Geocoding.Domain.ForwardGeocodeRequest;
using CoreReverseGeocodeMatch = Honua.Core.Features.Geocoding.Domain.ReverseGeocodeMatch;
using CoreReverseGeocodeRequest = Honua.Core.Features.Geocoding.Domain.ReverseGeocodeRequest;
using CoreSuggestGeocodeRequest = Honua.Core.Features.Geocoding.Domain.SuggestGeocodeRequest;
using CoreBatchGeocodeRequest = Honua.Core.Features.Geocoding.Domain.BatchGeocodeRequest;
using CoreGeocodeSuggestion = Honua.Core.Features.Geocoding.Domain.GeocodeSuggestion;
using CoreGeocodeProviderException = Honua.Core.Features.Geocoding.Domain.GeocodeProviderException;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Geocoding;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not supported", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses")]
    public async Task BatchGeocode_ReturnsBadRequest_WhenExplicitProviderDoesNotSupportBatch()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"test address"}}]""";
        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&provider=fake&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
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
            SupportsBiasing: true)));
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
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"1600 Pennsylvania Ave NW"}}]""";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
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
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        var addresses = """{"records":[{"attributes":{"OBJECTID":1,"SingleLine":"1600 Pennsylvania Ave NW"}},{"attributes":{"OBJECTID":2,"SingleLine":"350 Fifth Avenue, New York"}}]}""";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
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
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        var addresses = """{"records":[{"attributes":{"OBJECTID":1,"SingleLine":"1600 Pennsylvania Ave NW"}}]}""";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
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
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/geocodeAddresses?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/geocodeAddresses?records=not-valid-json&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        { MaxBatchSize = 2 }));
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"addr1"}},{"attributes":{"SingleLine":"addr2"}},{"attributes":{"SingleLine":"addr3"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&provider=fake&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
            SupportsBiasing: true)));
        using var client = factory.CreateClient();

        var records = """[{"attributes":{"SingleLine":"valid address"}},{"attributes":{}},{"attributes":{"SingleLine":"another valid"}}]""";

        using var response = await client.GetAsync($"/rest/services/World/GeocodeServer/geocodeAddresses?records={Uri.EscapeDataString(records)}&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task FindAddressCandidates_Post_ReturnsGeoServicesPayload()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
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

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{locatorName}/GeocodeServer/findAddressCandidates")]
    public async Task ForwardGeocode_ReturnsBadRequest_WhenExplicitProviderNotFound()
    {
        using var factory = CreateDefaultFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/services/World/GeocodeServer/findAddressCandidates?singleLine=test&provider=nonexistent&f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        using var factory = CreateFailoverFactory();
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
        Assert.True(
            response.StatusCode == HttpStatusCode.InternalServerError ||
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected error status code but got {response.StatusCode}");
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
        var postContent = new FormUrlEncodedContent(new Dictionary<string, string>
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    private static readonly CoreGeocodeProviderCapabilities DefaultCapabilities = new(
        SupportsSuggest: true,
        SupportsBatch: false,
        SupportsStructuredInput: false,
        SupportsBiasing: true);

    private static WebApplicationFactory<Program> CreateDefaultFactory()
        => CreateFactory(new FakeGeocodeProvider(DefaultCapabilities));

    private static WebApplicationFactory<Program> CreateFactory(FakeGeocodeProvider fakeProvider)
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

    private static WebApplicationFactory<Program> CreateFailoverFactory()
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

            var candidate = new CoreGeocodeCandidate(
                Address: "1600 Pennsylvania Ave NW",
                X: -77.03655,
                Y: 38.89768,
                Score: 99.1,
                Attributes: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Provider"] = Name,
                    ["Match_addr"] = "1600 Pennsylvania Ave NW"
                },
                ProviderId: "fake-1");

            return Task.FromResult<IReadOnlyList<CoreGeocodeCandidate>>([candidate]);
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

            var suggestion = new CoreGeocodeSuggestion("Honua HQ", "fake-suggest-1", false);
            return Task.FromResult<IReadOnlyList<CoreGeocodeSuggestion>>([suggestion]);
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
