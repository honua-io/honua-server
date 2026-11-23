// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

// Valid JSON of the wrong kind in a caller parameter must be reported as invalid input
// (code 400), not escape as an InvalidOperationException mapped to a server error.
public sealed class FeatureServerWrongKindJsonParameterFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, capabilities: ["Query", "Sync"]);
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerWrongKindJsonParameterTests(FeatureServerWrongKindJsonParameterFixture fixture)
    : IClassFixture<FeatureServerWrongKindJsonParameterFixture>
{
    private static readonly string LayerPath =
        $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}";

    private readonly WebAppFixture _fixture = fixture.App;

    [IntegrationTheory]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    [InlineData("[1]")]
    [InlineData("5")]
    [InlineData("\"classBreaksDef\"")]
    public async Task GenerateRenderer_NonObjectClassificationDef_ReturnsBadRequest(string classificationDef)
    {
        using var response = await _fixture.Client.GetAsync(
            $"{LayerPath}/generateRenderer?classificationDef={Uri.EscapeDataString(classificationDef)}&f=json");

        await AssertBadRequestAsync(response, "Invalid classificationDef");
    }

    [IntegrationTheory]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData("[1]")]
    [InlineData("""[{"statisticType":1,"onStatisticField":"objectid","outStatisticFieldName":"n"}]""")]
    [InlineData("""[{"statisticType":"count","onStatisticField":["objectid"],"outStatisticFieldName":"n"}]""")]
    [InlineData("""[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":{"name":"n"}}]""")]
    public async Task Query_WronglyTypedOutStatisticsEntry_ReturnsBadRequest(string outStatistics)
    {
        using var response = await _fixture.Client.GetAsync(
            $"{LayerPath}/query?where=1%3D1&outStatistics={Uri.EscapeDataString(outStatistics)}&f=json");

        await AssertBadRequestAsync(response, "Invalid outStatistics");
    }

    [IntegrationTheory]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    [InlineData("""[{"id":"0","serverGen":1}]""")]
    [InlineData("""[{"id":0,"serverGen":"5"}]""")]
    [InlineData("""[{"id":{"value":0},"serverGen":1}]""")]
    public async Task ExtractChanges_NonNumericLayerServerGensMember_ReturnsBadRequest(string layerServerGens)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { layerServerGens, f = "json" }), Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges", content);

        await AssertBadRequestAsync(response, "Invalid layerServerGens parameter");
    }

    private static async Task AssertBadRequestAsync(HttpResponseMessage response, string expectedTitle)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString())
            .Should().Contain(expectedTitle, body);
    }
}
