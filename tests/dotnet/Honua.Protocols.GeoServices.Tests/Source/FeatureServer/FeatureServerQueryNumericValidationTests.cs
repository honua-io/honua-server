// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerQueryNumericValidationTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FeatureServerQueryNumericValidationTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string, string, string> NonFiniteQueryValues
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var parameter in new[] { "distance", "maxAllowableOffset" })
            foreach (var value in new[] { "NaN", "Infinity", "-Infinity", "1e999" })
            {
                data.Add("GET", parameter, value);
                data.Add("POST", parameter, value);
            }

            return data;
        }
    }

    public static TheoryData<string, string, string> FiniteQueryValues
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var parameter in new[] { "distance", "maxAllowableOffset" })
            foreach (var value in new[] { "", "0", "1" })
            {
                data.Add("GET", parameter, value);
                data.Add("POST", parameter, value);
            }

            return data;
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(NonFiniteQueryValues))]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_NonFiniteNumericParameter_ReturnsClientError(string method, string parameter, string value)
    {
        using var response = await QueryAsync(method, parameter, value);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var document = JsonDocument.Parse(content);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("message").GetString().Should().Contain(parameter);
        document.RootElement.TryGetProperty("features", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [MemberData(nameof(FiniteQueryValues))]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_FiniteOrEmptyNumericParameter_ReturnsFeatures(string method, string parameter, string value)
    {
        using var response = await QueryAsync(method, parameter, value);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(content);
        document.RootElement.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Array);
    }

    private async Task<HttpResponseMessage> QueryAsync(string method, string parameter, string value)
    {
        var path = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query";
        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = "1=1",
            ["geometry"] = "{\"x\":0,\"y\":0,\"spatialReference\":{\"wkid\":4326}}",
            ["geometryType"] = "esriGeometryPoint",
            ["inSR"] = "4326",
            ["units"] = "esriSRUnit_Meter",
            [parameter] = value
        };
        using var form = new FormUrlEncodedContent(parameters);
        return method == "POST"
            ? await _fixture.Client.PostAsync(path, form)
            : await _fixture.Client.GetAsync(path + "?" + await form.ReadAsStringAsync());
    }
}
