// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

public sealed partial class GeometryServiceProjectTests
{
    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_ExplicitRealization_MatchesIndependentReference()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["inSR"] = "4269",
            ["outSR"] = "4326",
            ["transformation"] = "108190",
            ["geometries"] = """{"geometryType":"esriGeometryPoint","geometries":[{"x":-96,"y":41}]}"""
        });
        using var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var geometry = document.RootElement.GetProperty("geometries").EnumerateArray().Single();
        geometry.GetProperty("x").GetDouble().Should().BeApproximately(-96.00000801053693, 8e-8);
        geometry.GetProperty("y").GetDouble().Should().BeApproximately(41.00000720698309, 8e-8);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_TimedOperationWithEpoch_MatchesIndependentReference()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["inSR"] = "7912",
            ["outSR"] = "6319",
            ["transformation"] = "108574",
            ["coordinateEpoch"] = "2020",
            ["geometries"] = """{"geometryType":"esriGeometryPoint","geometries":[{"x":-96,"y":41,"z":0}]}"""
        });
        using var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var geometry = document.RootElement.GetProperty("geometries").EnumerateArray().Single();
        geometry.GetProperty("x").GetDouble().Should().BeApproximately(-95.99998731925608, 8e-8);
        geometry.GetProperty("y").GetDouble().Should().BeApproximately(40.999993417501, 8e-8);
    }

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_TimedOperationWithoutFiniteEpoch_ReturnsClientError(string? epoch)
    {
        var values = new Dictionary<string, string>
        {
            ["inSR"] = "7912",
            ["outSR"] = "6319",
            ["transformation"] = "108574",
            ["geometries"] = """{"geometryType":"esriGeometryPoint","geometries":[{"x":-96,"y":41}]}"""
        };
        if (epoch is not null)
        {
            values["coordinateEpoch"] = epoch;
        }

        using var content = new FormUrlEncodedContent(values);
        using var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        await response.AssertGeoServicesErrorAsync(400);
        (await response.Content.ReadAsStringAsync()).Should().Contain("coordinateEpoch");
    }
}
