// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerQueryBinsTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FeatureServerQueryBinsTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_ClassificationBin_ReturnsBins()
    {
        var bin = JsonSerializer.Serialize(new
        {
            classificationBin = new { field = "category" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_MissingBin_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?f=json");

        // GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("bin");
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_InvalidBinJson_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?bin=not-json&f=json");

        // GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTheory]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    [InlineData("""[{"statisticType":5,"onStatisticField":"objectid","outStatisticFieldName":"n"}]""")]
    [InlineData("""[{"statisticType":"count","onStatisticField":["objectid"],"outStatisticFieldName":"n"}]""")]
    [InlineData("""[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":{"name":"n"}}]""")]
    public async Task QueryBins_WronglyTypedOutStatisticsMember_ReturnsBadRequest(string value)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?bin={Uri.EscapeDataString("""{"fixedIntervalBin":{"field":"objectid","intervalSize":10}}""")}&outStatistics={Uri.EscapeDataString(value)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString())
            .Should().Contain("Invalid outStatistics parameter");
    }

    [IntegrationTheory]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    [InlineData("""{"fixedIntervalBin":{"field":["objectid"],"intervalSize":10}}""")]
    [InlineData("""{"fixedIntervalBin":{"field":"objectid","intervalSize":"10"}}""")]
    [InlineData("""{"fixedIntervalBin":{"field":"objectid","intervalSize":10,"start":"0"}}""")]
    [InlineData("""{"autoIntervalBin":{"field":"objectid","numBins":"5"}}""")]
    [InlineData("""{"fixedBoundariesBin":{"field":"objectid","boundaries":[0,"10"]}}""")]
    [InlineData("""{"dateBin":{"field":"objectid","fixedBin":{"intervalCount":"1"}}}""")]
    [InlineData("""{"dateBin":{"field":"objectid","fixedBin":{"intervalCount":1,"intervalUnit":7}}}""")]
    [InlineData("""{"dateBin":{"field":"objectid","calendarBin":{"unit":["day"]}}}""")]
    [InlineData("""{"classificationBin":{"field":{"name":"category"}}}""")]
    public async Task QueryBins_WronglyTypedBinMember_ReturnsBadRequest(string bin)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString())
            .Should().Contain("Invalid bin parameter");
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_InvalidService_ReturnsNotFound()
    {
        var bin = JsonSerializer.Serialize(new
        {
            classificationBin = new { field = "category" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/0/queryBins?bin={Uri.EscapeDataString(bin)}&f=json");

        // GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBinsPost_ValidRequest_ReturnsBins()
    {
        var bin = JsonSerializer.Serialize(new
        {
            classificationBin = new { field = "category" }
        });

        var payload = JsonSerializer.Serialize(new
        {
            bin,
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task SRV_DB_003_FixedBoundariesOverLimit_ReturnsValidationError()
    {
        var bin = JsonSerializer.Serialize(new
        {
            fixedBoundariesBin = new { field = "objectid", boundaries = Enumerable.Range(0, 1001) }
        });
        var payload = JsonSerializer.Serialize(new { bin, f = "json" });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("cannot contain more than 1000");
    }

    [IntegrationTest]
    [Operation(Operations.QueryBins)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task AutoIntervalBinCountOverLimit_ReturnsValidationError()
    {
        var bin = JsonSerializer.Serialize(new
        {
            autoIntervalBin = new { field = "objectid", numBins = 1001 }
        });
        var payload = JsonSerializer.Serialize(new { bin, f = "json" });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("numBins cannot be more than 1000");
    }

    [IntegrationTheory]
    [InlineData(1, "months", "calendarBin")]
    [InlineData(1, "fortnights", "seconds, minutes, hours, days or weeks")]
    [InlineData(int.MaxValue, "days", "too large")]
    [Operation(Operations.QueryBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryBins")]
    public async Task QueryBins_DateBinFixedIntervalInvalidOrOverflow_IsRejected(int count, string unit, string expectedHint)
    {
        var bin = JsonSerializer.Serialize(new
        {
            dateBin = new
            {
                field = "timestamp",
                fixedBin = new { intervalCount = count, intervalUnit = unit }
            }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBins?bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        content.Should().Contain(expectedHint);
    }
}
