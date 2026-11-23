// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed partial class Wfs20EndpointsTests
{
    private const string MatchCaseMarker = "MatchCaseMarker";

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_PropertyIsEqualToMatchCaseFalse_MatchesOtherCaseOnStringAndPlainValueOnInteger()
    {
        await using var fixture = await StartIsolatedApplicationAsync();
        var objectId = await fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, MatchCaseMarker);

        var objectIdText = objectId.ToString(CultureInfo.InvariantCulture);

        (await CountMatchesAsync(fixture, Comparison("name", "matchcasemarker", "matchCase=\"false\""))).Should().Be("1");
        (await CountMatchesAsync(fixture, Comparison("name", "matchcasemarker", string.Empty))).Should().Be("0");
        (await CountMatchesAsync(fixture, Comparison("objectid", objectIdText, "matchCase=\"false\""))).Should().Be("1");
    }

    private static string Comparison(string property, string literal, string matchCaseAttribute)
        => $"<fes:PropertyIsEqualTo {matchCaseAttribute}><fes:ValueReference>{property}</fes:ValueReference>"
            + $"<fes:Literal>{literal}</fes:Literal></fes:PropertyIsEqualTo>";

    private static async Task<string?> CountMatchesAsync(WebAppFixture fixture, string comparison)
    {
        var filter = $"<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\">{comparison}</fes:Filter>";
        var response = await fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=pub-layer-0&FILTER={Uri.EscapeDataString(filter)}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return System.Xml.Linq.XDocument.Parse(body).Root?.Attribute("numberMatched")?.Value;
    }
}
