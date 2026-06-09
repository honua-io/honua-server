// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Query)]
public sealed class Wfs20NumberMatchedPolicyTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Wfs20:NumberMatchedPolicy"] = "UnknownWhenExpensive"
                })));

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_GeoJsonOutput_WithUnknownNumberMatchedPolicy_OmitsNumberMatched()
    {
        const string outputFormat = "application/geo%2Bjson";
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&OUTPUTFORMAT={outputFormat}&COUNT=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        document.RootElement.TryGetProperty("numberMatched", out _).Should().BeFalse();
        document.RootElement.GetProperty("numberReturned").GetInt32().Should().BeLessOrEqualTo(1);

        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        if (features.Length > 0)
        {
            features[0].GetProperty("id").ValueKind.Should().BeOneOf(JsonValueKind.String, JsonValueKind.Number);
            features[0].GetProperty("properties").TryGetProperty("id", out _).Should().BeFalse();
            features[0].GetProperty("properties").TryGetProperty("objectid", out _).Should().BeFalse();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_ResultTypeHits_WithUnknownNumberMatchedPolicy_StillReturnsExactCount()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAME=test_layer&RESULTTYPE=hits");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("numberMatched=");
        content.Should().NotContain("numberMatched=\"unknown\"");
    }
}
