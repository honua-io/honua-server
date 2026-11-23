// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

[Collection("Database")]
[Protocol(TestProtocols.Wmts10)]
public sealed class WmtsGetTileParameterValidationTests : IClassFixture<WebAppFixture>
{
    private static readonly string WmtsRoot = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS";
    private static readonly string LayerId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
    private readonly WebAppFixture _fixture;

    public WmtsGetTileParameterValidationTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string, string?, string> InvalidGetTileParameters => new()
    {
        { "LAYER", null, Fault("MissingParameterValue", "layer") },
        { "LAYER", "no-such-layer", Fault("InvalidParameterValue", "layer") },
        { "STYLE", null, Fault("MissingParameterValue", "style") },
        { "STYLE", "no-such-style", Fault("InvalidParameterValue", "Style") },
        { "FORMAT", null, Fault("MissingParameterValue", "format") },
        { "FORMAT", "image/jpeg", Fault("InvalidParameterValue", "format") },
        { "TILEMATRIXSET", null, Fault("MissingParameterValue", "TileMatrixSet") },
        { "TILEMATRIX", null, Fault("MissingParameterValue", "TileMatrix") },
        { "TILEMATRIX", "-1", Fault("InvalidParameterValue", "TileMatrix") },
        { "TILEMATRIX", "abc", Fault("InvalidParameterValue", "TileMatrix") },
        { "TILEMATRIX", "99", Fault("InvalidParameterValue", "TileMatrix") },
        { "TILEROW", null, Fault("MissingParameterValue", "TileRow") },
        { "TILEROW", "-1", Fault("InvalidParameterValue", "TileRow") },
        { "TILEROW", "abc", Fault("InvalidParameterValue", "TileRow") },
        { "TILEROW", "99999999999", Fault("InvalidParameterValue", "TileRow") },
        { "TILEROW", "2", Fault("TileOutOfRange", "TileRow") },
        { "TILECOL", null, Fault("MissingParameterValue", "TileCol") },
        { "TILECOL", "2", Fault("TileOutOfRange", "TileCol") },
        { "VERSION", "2.0.0", Fault("InvalidParameterValue", "version") },
    };

    [IntegrationTheory]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [MemberData(nameof(InvalidGetTileParameters))]
    public async Task GetTile_InvalidParameter_ReturnsExceptionCodeAndLocator(string parameter, string? value, string expectedFault)
    {
        var response = await _fixture.Client.GetAsync(BuildGetTileUrl(parameter, value));

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain(expectedFault);
    }

    [IntegrationTheory]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS/{**restPath}")]
    [InlineData("default/WebMercatorQuad/0/0/0.png/")]
    [InlineData("default/WebMercatorQuad/0/0/0/extra.png")]
    [InlineData("default/WebMercatorQuad/0/0/0/128/128.txt/extra")]
    [InlineData("default/WebMercatorQuad/0/0/0/128/128/extra/1.txt")]
    public async Task Restful_PathWithUnexpectedSegmentCount_ReturnsNotFound(string pathAfterLayer)
    {
        var response = await _fixture.Client.GetAsync($"{WmtsRoot}/{LayerId}/{pathAfterLayer}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string BuildGetTileUrl(string parameter, string? value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SERVICE"] = "WMTS",
            ["REQUEST"] = "GetTile",
            ["VERSION"] = "1.0.0",
            ["LAYER"] = LayerId,
            ["STYLE"] = "default",
            ["FORMAT"] = "image/png",
            ["TILEMATRIXSET"] = "WebMercatorQuad",
            ["TILEMATRIX"] = "1",
            ["TILEROW"] = "0",
            ["TILECOL"] = "0",
        };

        if (value is null)
        {
            parameters.Remove(parameter);
        }
        else
        {
            parameters[parameter] = value;
        }

        var query = string.Join("&", parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        return $"{WmtsRoot}?{query}";
    }

    private static string Fault(string exceptionCode, string locator)
        => $"exceptionCode=\"{exceptionCode}\" locator=\"{locator}\"";
}
