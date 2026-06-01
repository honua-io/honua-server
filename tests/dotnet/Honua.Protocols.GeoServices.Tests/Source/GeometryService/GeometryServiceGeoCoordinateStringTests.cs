// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.GeometryService.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

/// <summary>
/// Covers the GeometryServer toGeoCoordinateString / fromGeoCoordinateString
/// operations (MGRS / USNG grid reference conversion).
/// </summary>
[Protocol(TestProtocols.GeometryService)]
[Collection("Database")]
public sealed class GeometryServiceGeoCoordinateStringTests : IAsyncLifetime
{

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ToGeoCoordinateString)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString")]
    public async Task ToGeoCoordinateString_Mgrs_ReturnsGridStrings()
    {
        var body = """
        {
            "sr": 4326,
            "coordinates": [[-77.0353, 38.8895]],
            "conversionType": "MGRS"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString", content);

        response.Be200Ok();

        var result = await DeserializeToAsync(response);
        result.Should().NotBeNull();
        result!.Strings.Should().ContainSingle();
        // MGRS conventionally has no spaces. The Washington Monument reference value.
        result.Strings![0].Should().Be("18SUJ2347806483");
    }

    [IntegrationTest]
    [Operation(Operations.ToGeoCoordinateString)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString")]
    public async Task ToGeoCoordinateString_Usng_ReturnsSpacedGridStrings()
    {
        var body = """
        {
            "sr": 4326,
            "coordinates": [[-77.0353, 38.8895]],
            "conversionType": "USNG"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString", content);

        response.Be200Ok();

        var result = await DeserializeToAsync(response);
        result.Should().NotBeNull();
        result!.Strings.Should().ContainSingle();
        result.Strings![0].Should().Be("18S UJ 23478 06483");
    }

    [IntegrationTest]
    [Operation(Operations.ToGeoCoordinateString)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString")]
    public async Task ToGeoCoordinateString_GetWithQueryString_ReturnsGridStrings()
    {
        var coordinates = Uri.EscapeDataString("[[-77.0353, 38.8895]]");
        // Literal route path  so the EndpointRegistry drift
        // scanner's per-method source check finds the GET request for this route.
        var url = $"/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString?sr=4326&coordinates={coordinates}&conversionType=MGRS";

        var response = await _fixture.Client.GetAsync(url);

        response.Be200Ok();

        var result = await DeserializeToAsync(response);
        result.Should().NotBeNull();
        result!.Strings.Should().ContainSingle();
        result.Strings![0].Should().StartWith("18SUJ");
    }

    [IntegrationTest]
    [Operation(Operations.ToGeoCoordinateString)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString")]
    public async Task ToGeoCoordinateString_BatchCoordinates_ReturnsOneStringPerInput()
    {
        var body = """
        {
            "sr": 4326,
            "coordinates": [[-77.0353, 38.8895], [2.3522, 48.8566], [151.2093, -33.8688]],
            "conversionType": "MGRS"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString", content);

        response.Be200Ok();

        var result = await DeserializeToAsync(response);
        result.Should().NotBeNull();
        result!.Strings.Should().HaveCount(3);
    }

    [IntegrationTest]
    [Operation(Operations.ToGeoCoordinateString)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString")]
    public async Task ToGeoCoordinateString_UnsupportedConversionType_Returns400()
    {
        var body = """
        {
            "sr": 4326,
            "coordinates": [[-77.0353, 38.8895]],
            "conversionType": "GARS"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString", content);

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.ToGeoCoordinateString)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString")]
    public async Task ToGeoCoordinateString_MissingCoordinates_Returns400()
    {
        var body = """{"sr": 4326, "conversionType": "MGRS"}""";
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString", content);

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.FromGeoCoordinateString)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString")]
    public async Task FromGeoCoordinateString_Mgrs_ReturnsCoordinates()
    {
        var body = """
        {
            "sr": 4326,
            "strings": ["18S UJ 23478 06483"],
            "conversionType": "MGRS"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString", content);

        response.Be200Ok();

        var result = await DeserializeFromAsync(response);
        result.Should().NotBeNull();
        result!.Coordinates.Should().ContainSingle();
        result.Coordinates![0][0].Should().BeApproximately(-77.0353, 0.01);
        result.Coordinates[0][1].Should().BeApproximately(38.8895, 0.01);
    }

    [IntegrationTest]
    [Operation(Operations.FromGeoCoordinateString)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString")]
    public async Task FromGeoCoordinateString_GetWithQueryString_ReturnsCoordinates()
    {
        var strings = Uri.EscapeDataString("""["18SUJ2347806483"]""");
        // Use the literal route path  so the EndpointRegistry
        // drift scanner's per-method source check finds the GET request for this route.
        var url = $"/rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString?sr=4326&strings={strings}&conversionType=MGRS";

        var response = await _fixture.Client.GetAsync(url);

        response.Be200Ok();

        var result = await DeserializeFromAsync(response);
        result.Should().NotBeNull();
        result!.Coordinates.Should().ContainSingle();
        result.Coordinates![0][1].Should().BeApproximately(38.8895, 0.01);
    }

    [IntegrationTest]
    [Operation(Operations.FromGeoCoordinateString)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString")]
    public async Task FromGeoCoordinateString_RoundTripsWithToGeoCoordinateString()
    {
        // Encode, then decode, asserting we recover the original coordinate.
        var toBody = """
        {
            "sr": 4326,
            "coordinates": [[-77.0739, 38.9587]],
            "conversionType": "MGRS"
        }
        """;
        var toResponse = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/toGeoCoordinateString", new StringContent(toBody, Encoding.UTF8, "application/json"));
        toResponse.Be200Ok();
        var encoded = (await DeserializeToAsync(toResponse))!.Strings![0];

        var fromBody = $$"""
        {
            "sr": 4326,
            "strings": ["{{encoded}}"],
            "conversionType": "MGRS"
        }
        """;
        var fromResponse = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString", new StringContent(fromBody, Encoding.UTF8, "application/json"));
        fromResponse.Be200Ok();
        var decoded = (await DeserializeFromAsync(fromResponse))!.Coordinates![0];

        decoded[0].Should().BeApproximately(-77.0739, 0.0001);
        decoded[1].Should().BeApproximately(38.9587, 0.0001);
    }

    [IntegrationTest]
    [Operation(Operations.FromGeoCoordinateString)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString")]
    public async Task FromGeoCoordinateString_MissingStrings_Returns400()
    {
        var body = """{"sr": 4326, "conversionType": "MGRS"}""";
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/fromGeoCoordinateString", content);

        response.Be400BadRequest();
    }

    private static async Task<GeometryServiceToGeoCoordinateStringResponse?> DeserializeToAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(
            content, GeometryServiceJsonContext.Default.GeometryServiceToGeoCoordinateStringResponse);
    }

    private static async Task<GeometryServiceFromGeoCoordinateStringResponse?> DeserializeFromAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(
            content, GeometryServiceJsonContext.Default.GeometryServiceFromGeoCoordinateStringResponse);
    }
}
