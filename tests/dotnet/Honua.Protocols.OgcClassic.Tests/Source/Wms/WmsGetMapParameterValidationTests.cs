// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class WmsGetMapParameterValidationTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public WmsGetMapParameterValidationTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTheory]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InlineData("CRS", "EPSG:0", "InvalidCRS")]
    [InlineData("CRS", "EPSG:-4326", "InvalidCRS")]
    [InlineData("CRS", "not-a-crs", "InvalidCRS")]
    [InlineData("CRS", null, "InvalidCRS")]
    [InlineData("STYLES", null, "MissingParameterValue")]
    [InlineData("WIDTH", null, "MissingParameterValue")]
    [InlineData("WIDTH", "0", "InvalidParameterValue")]
    [InlineData("WIDTH", "-1", "InvalidParameterValue")]
    [InlineData("WIDTH", "abc", "InvalidParameterValue")]
    [InlineData("WIDTH", "4097", "InvalidParameterValue")]
    [InlineData("HEIGHT", "4097", "InvalidParameterValue")]
    [InlineData("FORMAT", null, "MissingParameterValue")]
    [InlineData("FORMAT", "image/gif", "InvalidFormat")]
    [InlineData("EXCEPTIONS", "application/x-unknown", "InvalidFormat")]
    [InlineData("TRANSPARENT", "maybe", "InvalidParameterValue")]
    [InlineData("BGCOLOR", "0xZZZZZZ", "InvalidParameterValue")]
    [InlineData("VERSION", "1.2.0", "InvalidParameterValue")]
    [InlineData("BBOX", "1,2,3", "InvalidParameterValue")]
    [InlineData("BBOX", "a,b,c,d", "InvalidParameterValue")]
    [InlineData("STYLES", "no-such-style", "StyleNotDefined")]
    [InlineData("STYLES", ",", "StyleNotDefined")]
    public async Task GetMap_InvalidParameter_ReturnsServiceExceptionCode(string parameter, string? value, string expectedCode)
    {
        var response = await _fixture.Client.GetAsync(BuildGetMapUrl("1.3.0", parameter, value));

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain($"code=\"{expectedCode}\"");
    }

    [IntegrationTheory]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InlineData("EPSG:0")]
    [InlineData("not-a-crs")]
    public async Task GetMap111_InvalidSrs_ReturnsInvalidSrsCode(string srs)
    {
        var response = await _fixture.Client.GetAsync(BuildGetMapUrl("1.1.1", "CRS", srs));

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("code=\"InvalidSRS\"");
        content.Should().NotContain("code=\"InvalidCRS\"");
    }

    private static string BuildGetMapUrl(string version, string parameter, string? value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SERVICE"] = "WMS",
            ["REQUEST"] = "GetMap",
            ["VERSION"] = version,
            ["LAYERS"] = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["STYLES"] = string.Empty,
            ["CRS"] = "EPSG:4326",
            ["BBOX"] = "-90,-180,90,180",
            ["WIDTH"] = "64",
            ["HEIGHT"] = "64",
            ["FORMAT"] = "image/png",
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
        return $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?{query}";
    }
}
