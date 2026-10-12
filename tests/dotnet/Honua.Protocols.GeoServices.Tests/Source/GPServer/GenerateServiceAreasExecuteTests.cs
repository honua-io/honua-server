// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>Real HTTP evidence for the synchronous service-area projection and its provider gates.</summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GPServer)]
public sealed class GenerateServiceAreasExecuteTests(GenerateServiceAreasExecuteTestsFixture fixture)
    : IClassFixture<GenerateServiceAreasExecuteTestsFixture>
{
    private const string Facilities = """{"spatialReference":{"wkid":4326},"features":[{"geometry":{"x":-157.858333,"y":21.306944}},{"geometry":{"x":-157.862,"y":21.31}}]}""";

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task ExecuteGet_PublishedNaServerAlias_ReturnsCanonicalPolygons()
    {
        fixture.Requests.Clear();
        using var content = new FormUrlEncodedContent(Inputs());
        using var response = await fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/NAServer/GenerateServiceAreas/execute?{await content.ReadAsStringAsync()}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        result.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        fixture.Requests.Should().ContainSingle();
        fixture.Requests.Single().Facilities.Should().Equal(
            new RoutePoint(-157.858333, 21.306944), new RoutePoint(-157.862, 21.31));
        fixture.Requests.Single().Breaks.Should().Equal(5d, 10d, 15d);
        var results = result.RootElement.GetProperty("results");
        results[0].GetProperty("value").GetProperty("features").GetArrayLength().Should().Be(6);
        results[1].GetProperty("value").GetBoolean().Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData("GPServer", false, "Away From Facility")]
    [InlineData("GPServer", true, "Away From Facility")]
    [InlineData("NAServer", false, "Away From Facility")]
    [InlineData("NAServer", true, "Away From Facility")]
    [InlineData("GPServer", false, "Towards Facility")]
    [InlineData("GPServer", true, "Towards Facility")]
    [InlineData("NAServer", false, "Towards Facility")]
    [InlineData("NAServer", true, "Towards Facility")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_MatchesCanonicalServiceAreaRequestAndEveryPolygon(string service, bool post, string direction)
    {
        fixture.Requests.Clear();
        var parameters = Inputs();
        parameters["Travel_Direction"] = direction;
        using var actual = await ExecuteAsync(service, post, parameters);
        var gpRequest = fixture.Requests.Single();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["facilities"] = Facilities,
            ["defaultBreaks"] = "5,10,15",
            ["travelMode"] = "walking",
            ["travelDirection"] = direction == "Towards Facility" ? "esriNATravelDirectionToFacility" : "esriNATravelDirectionFromFacility",
        });
        using var response = await fixture.Client.PostAsync($"/rest/services/{WebAppFixture.TestServiceId}/NAServer/ServiceArea/solveServiceArea", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var canonical = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        fixture.Requests.Should().HaveCount(2);
        gpRequest.Should().BeEquivalentTo(fixture.Requests.Last());
        gpRequest.Facilities.Should().Equal(new RoutePoint(-157.858333, 21.306944), new RoutePoint(-157.862, 21.31));
        gpRequest.Breaks.Should().Equal(5d, 10d, 15d);
        var results = actual.RootElement.GetProperty("results");
        results[0].GetProperty("paramName").GetString().Should().Be("Service_Areas");
        results[0].GetProperty("dataType").GetString().Should().Be("GPFeatureRecordSetLayer");
        var polygons = results[0].GetProperty("value");
        foreach (var name in new[] { "geometryType", "spatialReference" })
        {
            polygons.GetProperty(name).GetRawText().Should().Be(canonical.RootElement.GetProperty("saPolygons").GetProperty(name).GetRawText());
        }
        polygons.GetProperty("features").GetArrayLength().Should().Be(6);
        var actualFeatures = polygons.GetProperty("features");
        var canonicalFeatures = canonical.RootElement.GetProperty("saPolygons").GetProperty("features");
        for (var index = 0; index < actualFeatures.GetArrayLength(); index++)
        {
            actualFeatures[index].GetProperty("geometry").GetRawText().Should().Be(canonicalFeatures[index].GetProperty("geometry").GetRawText());
            foreach (var attribute in canonicalFeatures[index].GetProperty("attributes").EnumerateObject())
            {
                actualFeatures[index].GetProperty("attributes").GetProperty(attribute.Name).GetRawText().Should().Be(attribute.Value.GetRawText());
            }
            actualFeatures[index].GetProperty("attributes").GetProperty("FacilityOID").ValueKind.Should().Be(JsonValueKind.Null);
        }
        polygons.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString())
            .Should().Equal("ObjectID", "FacilityID", "FacilityOID", "Name", "FromBreak", "ToBreak");
        results[1].GetProperty("paramName").GetString().Should().Be("Solve_Succeeded");
        results[1].GetProperty("dataType").GetString().Should().Be("GPBoolean");
        results[1].GetProperty("value").GetBoolean().Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_InvalidOrUnsupportedInputs_NeverReachProvider(string service)
    {
        var cases = new (string Key, string Value)[]
        {
            ("Break_Units", "Kilometers"), ("Break_Values", "NaN"), ("Break_Values", "Infinity"),
            ("Break_Values", "0 5"), ("Break_Values", "-1 5"), ("Break_Values", ""),
            ("defaultBreaks", "1,2"), ("Travel_Direction", "sideways"),
            ("travelDirection", "esriNATravelDirectionToFacility"), ("travelMode", "driving"),
            ("Travel_Mode", "flying"), ("Detailed_Polygons", "true"), ("f", "xml"),
            ("Facilities", ""), ("Facilities", "invalid"),
            ("Point_Barriers", """{"features":[{"geometry":{"x":-157.86,"y":21.31}}]}"""),
        };
        fixture.Requests.Clear();
        foreach (var (key, value) in cases)
        {
            var parameters = Inputs();
            parameters[key] = value;
            using var result = await ExecuteAsync(service, true, parameters);
            result.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400, key);
            result.RootElement.TryGetProperty("results", out _).Should().BeFalse();
        }
        fixture.Requests.Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("GPServer", false)]
    [InlineData("NAServer", false)]
    [InlineData("GPServer", true)]
    [InlineData("NAServer", true)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_DisabledSolveOrDirection_RejectsBeforeProvider(string service, bool directionOnly)
    {
        var original = fixture.Provider.Capabilities;
        try
        {
            fixture.Provider.Capabilities = original with
            {
                SupportsServiceArea = directionOnly,
                SupportedTravelDirections = [ServiceAreaTravelDirection.FromFacility],
            };
            fixture.Requests.Clear();
            var parameters = Inputs();
            parameters["Travel_Direction"] = "Towards Facility";
            using var result = await ExecuteAsync(service, true, parameters);
            result.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
            fixture.Requests.Should().BeEmpty();
        }
        finally
        {
            fixture.Provider.Capabilities = original;
        }
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/{layerName}")]
    public async Task Metadata_DefaultsExecuteAndRemainSynchronous(string service)
    {
        using var response = await fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/{service}/GenerateServiceAreas?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeSynchronous");
        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["Facilities"] = Facilities,
        };
        foreach (var parameter in metadata.RootElement.GetProperty("parameters").EnumerateArray())
        {
            if (parameter.GetProperty("direction").GetString() == "esriGPParameterDirectionInput" &&
                parameter.GetProperty("name").GetString() != "Facilities" && parameter.GetProperty("defaultValue").ValueKind == JsonValueKind.String)
            {
                parameters[parameter.GetProperty("name").GetString()!] = parameter.GetProperty("defaultValue").GetString()!;
            }
        }
        fixture.Requests.Clear();
        using var result = await ExecuteAsync(service, true, parameters);
        fixture.Requests.Should().ContainSingle();
        fixture.Requests.Single().Breaks.Should().Equal(5d, 10d, 15d);
        result.RootElement.GetProperty("results")[1].GetProperty("value").GetBoolean().Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_CaseInsensitiveGpNamesAndTravelModeObject_PreserveCanonicalValues(string service)
    {
        var parameters = Inputs().ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value);
        parameters["travel_mode"] = """{"name":"walking"}""";
        fixture.Requests.Clear();

        using var result = await ExecuteAsync(service, true, parameters);

        var request = fixture.Requests.Single();
        request.TravelMode.Should().Be("walking");
        request.Breaks.Should().Equal(5d, 10d, 15d);
        request.TravelDirection.Should().Be(ServiceAreaTravelDirection.FromFacility);
        request.Facilities.Should().Equal(new RoutePoint(-157.858333, 21.306944), new RoutePoint(-157.862, 21.31));
        result.RootElement.GetProperty("results")[0].GetProperty("value").GetProperty("features").GetArrayLength().Should().Be(6);
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_NoReachablePolygons_ReturnsFalseAndGpWarning(string service)
    {
        fixture.Provider.ReturnNoPolygons = true;
        try
        {
            using var result = await ExecuteAsync(service, true, Inputs());
            result.RootElement.GetProperty("results")[0].GetProperty("value").GetProperty("features").GetArrayLength().Should().Be(0);
            result.RootElement.GetProperty("results")[1].GetProperty("value").GetBoolean().Should().BeFalse();
            var messages = result.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            messages.Should().ContainSingle();
            messages[0].GetProperty("type").GetString().Should().Be("esriJobMessageTypeWarning");
            messages[0].GetProperty("description").GetString().Should().Be("No service-area polygons could be generated for the supplied facilities and breaks.");
        }
        finally
        {
            fixture.Provider.ReturnNoPolygons = false;
        }
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_FacilityIdentityAndDefaultSettings_ArePreserved(string service)
    {
        const string facilities = """{"features":[{"geometry":{"x":-157.858333,"y":21.306944},"attributes":{"ObjectID":91,"Name":"North Station","Breaks":null,"AdditionalTime":0,"AdditionalDistance":0,"AdditionalCost":0,"CurbApproach":0}},{"geometry":{"x":-157.862,"y":21.31},"attributes":{"objectid":7,"name":"South Station","Breaks":""}}]}""";
        var parameters = Inputs();
        parameters["Facilities"] = facilities;
        fixture.Requests.Clear();

        using var result = await ExecuteAsync(service, true, parameters);

        fixture.Requests.Should().ContainSingle();
        var polygons = result.RootElement.GetProperty("results")[0].GetProperty("value").GetProperty("features");
        polygons.GetArrayLength().Should().Be(6);
        for (var index = 0; index < polygons.GetArrayLength(); index++)
        {
            var attributes = polygons[index].GetProperty("attributes");
            var facilityId = index / 3;
            attributes.GetProperty("ObjectID").GetInt32().Should().Be(index + 1);
            attributes.GetProperty("FacilityID").GetInt32().Should().Be(facilityId);
            attributes.GetProperty("FacilityOID").GetInt32().Should().Be(facilityId == 0 ? 91 : 7);
            attributes.GetProperty("Name").GetString().Should().Be(
                $"{(facilityId == 0 ? "North Station" : "South Station")} : {(index % 3) * 5} - {((index % 3) + 1) * 5}");
        }
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_UnsupportedFacilitySettings_RejectBeforeProvider(string service)
    {
        foreach (var attributes in new[]
                 {
                     """{"Breaks":"3 6"}""", """{"AdditionalTime":1}""", """{"AdditionalDistance":1}""",
                     """{"AdditionalCost":1}""", """{"CurbApproach":1}""", """{"CurbApproach":3}""",
                     """{"AdditionalTime":"0"}""", """{"ObjectID":1.5}""", """{"Name":1}""", """{"CustomAttribute":1}""",
                 })
        {
            var parameters = Inputs();
            parameters["Facilities"] = "{\"features\":[{\"geometry\":{\"x\":-157.858333,\"y\":21.306944},\"attributes\":" + attributes + "}]}";
            fixture.Requests.Clear();

            using var result = await ExecuteAsync(service, true, parameters);

            result.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
            fixture.Requests.Should().BeEmpty();
        }

        var invalidGeometry = Inputs();
        invalidGeometry["Facilities"] = """{"features":[{"geometry":{"rings":[]},"attributes":{"ObjectID":91}},{"geometry":{"x":-157.862,"y":21.31},"attributes":{"ObjectID":7}}]}""";
        using var rejectedGeometry = await ExecuteAsync(service, true, invalidGeometry);
        rejectedGeometry.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        fixture.Requests.Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("GPServer", "env:outSR", "3857")]
    [InlineData("NAServer", "env:outSR", "3857")]
    [InlineData("GPServer", "env:outSR", "{\"latestWkid\":3857}")]
    [InlineData("NAServer", "env:outSR", "{\"latestWkid\":3857}")]
    [InlineData("GPServer", "context", "{\"outSR\":3857}")]
    [InlineData("NAServer", "context", "{\"outSR\":3857}")]
    [InlineData("GPServer", "context", "{\"outSR\":{\"wkid\":3857},\"extent\":null}")]
    [InlineData("NAServer", "context", "{\"outSR\":{\"wkid\":3857},\"extent\":null}")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_GpOutputSpatialReference_MatchesCanonicalProjectedPolygons(string service, string key, string value)
    {
        var parameters = Inputs();
        parameters[key] = value;
        fixture.Requests.Clear();
        using var actual = await ExecuteAsync(service, true, parameters);
        var projectedRequest = fixture.Requests.Single();
        projectedRequest.OutSrid.Should().Be(3857);
        projectedRequest.InSrid.Should().Be(4326);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["facilities"] = Facilities,
            ["defaultBreaks"] = "5,10,15",
            ["travelMode"] = "walking",
            ["outSR"] = "3857",
        });
        using var response = await fixture.Client.PostAsync($"/rest/services/{WebAppFixture.TestServiceId}/NAServer/ServiceArea/solveServiceArea", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var canonical = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        projectedRequest.Should().BeEquivalentTo(fixture.Requests.Last());
        var polygons = actual.RootElement.GetProperty("results")[0].GetProperty("value");
        polygons.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
        var features = polygons.GetProperty("features");
        var expected = canonical.RootElement.GetProperty("saPolygons").GetProperty("features");
        features.GetArrayLength().Should().Be(6);
        for (var i = 0; i < features.GetArrayLength(); i++)
        {
            features[i].GetProperty("geometry").GetRawText().Should().Be(expected[i].GetProperty("geometry").GetRawText());
        }
        var first = features[0].GetProperty("geometry").GetProperty("rings")[0][0];
        first[0].GetDouble().Should().BeApproximately(-17568236.902113102, 0.001);
        first[1].GetDouble().Should().BeApproximately(2428516.23997281, 0.001);
    }

    [IntegrationTheory]
    [InlineData("GPServer", "env:outSR", "102100")]
    [InlineData("NAServer", "env:outSR", "102100")]
    [InlineData("GPServer", "context", "{\"outSR\":102113}")]
    [InlineData("NAServer", "context", "{\"outSR\":102113}")]
    [InlineData("GPServer", "outSR", "{\"wkid\":102100}")]
    [InlineData("NAServer", "outSR", "{\"wkid\":102100}")]
    [InlineData("GPServer", "env:outSR", "{\"wkid\":102113}")]
    [InlineData("NAServer", "env:outSR", "{\"wkid\":102113}")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_WebMercatorAliases_AreNormalizedBeforeProvider(string service, string key, string value)
    {
        var parameters = Inputs();
        parameters[key] = value;
        fixture.Requests.Clear();
        using var result = await ExecuteAsync(service, true, parameters);
        result.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        fixture.Requests.Single().OutSrid.Should().Be(3857);
        fixture.Requests.Single().InSrid.Should().Be(4326);
        result.RootElement.GetProperty("results")[0].GetProperty("value")
            .GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
    }

    [IntegrationTheory]
    [InlineData("GPServer", "env:outSR", "3857")]
    [InlineData("NAServer", "env:outSR", "3857")]
    [InlineData("GPServer", "context", "{\"outSR\":3857}")]
    [InlineData("NAServer", "context", "{\"outSR\":3857}")]
    [InlineData("GPServer", "outSR", "3857")]
    [InlineData("NAServer", "outSR", "3857")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_UndeclaredFacilities_KeepAdvertisedInputDefault(string service, string key, string value)
    {
        var parameters = Inputs();
        parameters["Facilities"] = Facilities.Replace("\"spatialReference\":{\"wkid\":4326},", "", StringComparison.Ordinal);
        parameters[key] = value;
        fixture.Requests.Clear();
        using var result = await ExecuteAsync(service, true, parameters);
        result.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        var request = fixture.Requests.Single();
        request.InSrid.Should().Be(4326);
        request.OutSrid.Should().Be(3857);
        request.Facilities.Should().Equal(new RoutePoint(-157.858333, 21.306944), new RoutePoint(-157.862, 21.31));
        var first = result.RootElement.GetProperty("results")[0].GetProperty("value")
            .GetProperty("features")[0].GetProperty("geometry").GetProperty("rings")[0][0];
        first[0].GetDouble().Should().BeApproximately(-17568236.902113102, 0.001);
        first[1].GetDouble().Should().BeApproximately(2428516.23997281, 0.001);
    }

    [IntegrationTheory]
    [InlineData("GPServer", false)]
    [InlineData("NAServer", false)]
    [InlineData("GPServer", true)]
    [InlineData("NAServer", true)]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_ExplicitFacilityReference_RemainsAuthoritative(string service, bool explicitInSr)
    {
        var parameters = Inputs();
        parameters["Facilities"] = explicitInSr
            ? Facilities.Replace("\"spatialReference\":{\"wkid\":4326},", "", StringComparison.Ordinal)
            : Facilities.Replace("4326", "3857", StringComparison.Ordinal);
        if (explicitInSr)
        {
            parameters["inSR"] = "3857";
        }
        parameters["env:outSR"] = "4326";
        fixture.Requests.Clear();
        using var result = await ExecuteAsync(service, true, parameters);
        result.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        fixture.Requests.Single().InSrid.Should().Be(3857);
        fixture.Requests.Single().OutSrid.Should().Be(4326);
    }

    [IntegrationTheory]
    [InlineData("GPServer", null, ServiceAreaTravelDirection.FromFacility)]
    [InlineData("NAServer", null, ServiceAreaTravelDirection.FromFacility)]
    [InlineData("GPServer", "Away From Facility", ServiceAreaTravelDirection.FromFacility)]
    [InlineData("NAServer", "Away From Facility", ServiceAreaTravelDirection.FromFacility)]
    [InlineData("GPServer", "Towards Facility", ServiceAreaTravelDirection.ToFacility)]
    [InlineData("NAServer", "Towards Facility", ServiceAreaTravelDirection.ToFacility)]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_ProviderWithoutDirectionDistinction_AllowsCanonicalDirection(
        string service, string? direction, ServiceAreaTravelDirection expected)
    {
        var original = fixture.Provider.Capabilities;
        try
        {
            fixture.Provider.Capabilities = original with { SupportedTravelDirections = [] };
            var parameters = Inputs();
            if (direction is null)
            {
                parameters.Remove("Travel_Direction");
            }
            else
            {
                parameters["Travel_Direction"] = direction;
            }
            fixture.Requests.Clear();
            using var result = await ExecuteAsync(service, true, parameters);
            result.RootElement.TryGetProperty("error", out _).Should().BeFalse();
            fixture.Requests.Single().TravelDirection.Should().Be(expected);
            result.RootElement.GetProperty("results")[0].GetProperty("value").GetProperty("features")
                .GetArrayLength().Should().Be(6);
        }
        finally
        {
            fixture.Provider.Capabilities = original;
        }
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_MatchingOutputSpatialReferenceAliases_AreHonored(string service)
    {
        var parameters = Inputs();
        parameters["outSR"] = "{\"wkid\":102100}";
        parameters["env:outSR"] = "3857";
        parameters["context"] = "{\"outSR\":3857}";
        fixture.Requests.Clear();
        using var result = await ExecuteAsync(service, false, parameters);
        result.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        fixture.Requests.Single().OutSrid.Should().Be(3857);
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/GenerateServiceAreas/execute")]
    public async Task Execute_MalformedConflictingOrUnsupportedGpEnvironment_NeverSolves(string service)
    {
        Dictionary<string, string>[] invalid =
        [
            new() { ["env:outSR"] = "invalid" },
            new() { ["env:outSR"] = "-1" },
            new() { ["env:outSR"] = "{\"wkid\":3857.5}" },
            new() { ["context"] = "not-json" },
            new() { ["context"] = "[]" },
            new() { ["context"] = "{\"outSR\":true}" },
            new() { ["context"] = "{\"outSR\":{\"wkt\":\"unsupported\"}}" },
            new() { ["context"] = "{\"outSR\":3857}", ["env:outSR"] = "4326" },
            new() { ["env:outSR"] = "3857", ["outSR"] = "4326" },
            new() { ["context"] = "{\"outSR\":3857}", ["outSR"] = "4326" },
            new() { ["context"] = "{\"processSR\":3857}" },
            new() { ["context"] = "{\"extent\":{\"xmin\":0}}" },
            new() { ["context"] = "{\"parallelProcessingFactor\":\"50%\"}" },
            new() { ["env:processSR"] = "3857" },
        ];
        foreach (var environment in invalid)
        {
            var parameters = Inputs();
            foreach (var entry in environment)
            {
                parameters[entry.Key] = entry.Value;
            }
            fixture.Requests.Clear();
            using var result = await ExecuteAsync(service, true, parameters);
            result.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
            fixture.Requests.Should().BeEmpty();
        }
    }

    private static Dictionary<string, string> Inputs() => new()
    {
        ["f"] = "json",
        ["Facilities"] = Facilities,
        ["Break_Values"] = "5 10 15",
        ["Break_Units"] = "Minutes",
        ["Travel_Direction"] = "Away From Facility",
        ["Travel_Mode"] = "walking",
    };

    private async Task<JsonDocument> ExecuteAsync(string service, bool post, Dictionary<string, string> parameters)
    {
        var url = $"/rest/services/{WebAppFixture.TestServiceId}/{service}/GenerateServiceAreas/execute";
        using var content = new FormUrlEncodedContent(parameters);
        using var response = post ? await fixture.Client.PostAsync(url, content) : await fixture.Client.GetAsync(url + "?" + await content.ReadAsStringAsync());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}

/// <summary>Records the actual canonical requests reached through HTTP while reusing existing solver output.</summary>
public sealed class GenerateServiceAreasExecuteTestsFixture : IAsyncLifetime
{
    public ConcurrentQueue<ServiceAreaSolveRequest> Requests { get; } = new();
    internal RecordingProvider Provider { get; }
    public WebAppFixture App { get; }
    public HttpClient Client { get; private set; } = null!;

    public GenerateServiceAreasExecuteTestsFixture()
    {
        Provider = new RecordingProvider(Requests);
        App = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        }).ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddSingleton<IRoutingProvider>(Provider);
        });
    }

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        Client = App.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }

    internal sealed class RecordingProvider(ConcurrentQueue<ServiceAreaSolveRequest> requests) : IRoutingProvider
    {
        private readonly TestRoutingProvider _canonical = new();
        public string Name => "test";
        public RoutingProviderCapabilities Capabilities { get; set; } = new() { SupportedTravelModes = ["driving", "walking"] };
        public bool ReturnNoPolygons { get; set; }
        public async Task<ServiceAreaSolveResult> SolveServiceAreaAsync(ServiceAreaSolveRequest request, CancellationToken cancellationToken = default)
        {
            requests.Enqueue(request);
            if (ReturnNoPolygons)
            {
                return new ServiceAreaSolveResult([]);
            }
            var result = await _canonical.SolveServiceAreaAsync(request, cancellationToken);
            if (request.OutSrid != 3857)
            {
                return result;
            }
            return new ServiceAreaSolveResult(result.Polygons.Select(polygon => polygon with
            {
                GeometryGeoJson = ProjectPolygon(polygon.GeometryGeoJson),
            }).ToArray());
        }

        private static string ProjectPolygon(string geoJson)
        {
            var geometry = JsonNode.Parse(geoJson)!;
            foreach (var point in geometry["coordinates"]!.AsArray().SelectMany(ring => ring!.AsArray()))
            {
                var coordinate = point!.AsArray();
                var longitude = coordinate[0]!.GetValue<double>();
                var latitude = coordinate[1]!.GetValue<double>();
                coordinate[0] = 6378137 * longitude * Math.PI / 180;
                coordinate[1] = 6378137 * Math.Log(Math.Tan(Math.PI / 4 + latitude * Math.PI / 360));
            }
            return geometry.ToJsonString();
        }
        public Task<RouteSolveResult> SolveRouteAsync(RouteSolveRequest request, CancellationToken cancellationToken = default) => _canonical.SolveRouteAsync(request, cancellationToken);
        public Task<ClosestFacilitySolveResult> SolveClosestFacilityAsync(ClosestFacilitySolveRequest request, CancellationToken cancellationToken = default) => _canonical.SolveClosestFacilityAsync(request, cancellationToken);
        public Task<OdCostMatrixSolveResult> SolveOdCostMatrixAsync(OdCostMatrixSolveRequest request, CancellationToken cancellationToken = default) => _canonical.SolveOdCostMatrixAsync(request, cancellationToken);
        public Task<LocationAllocationSolveResult> SolveLocationAllocationAsync(LocationAllocationSolveRequest request, CancellationToken cancellationToken = default) => _canonical.SolveLocationAllocationAsync(request, cancellationToken);
    }
}
