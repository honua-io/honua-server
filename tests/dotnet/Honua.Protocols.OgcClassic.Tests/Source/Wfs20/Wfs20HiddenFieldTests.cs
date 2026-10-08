// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using System.Net;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Query)]
public sealed class Wfs20HiddenFieldTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "category", Type = MetadataV2FieldType.String, Hidden = true
        });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("GetFeature", "")]
    [InlineData("GetFeature", "&OUTPUTFORMAT=csv")]
    [InlineData("GetFeature", "&OUTPUTFORMAT=application%2Fgeo%2Bjson")]
    [InlineData("DescribeFeatureType", "")]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeFeatureType")]
    public async Task Output_OmitsHiddenFields(string operation, string extra)
    {
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST={operation}&TYPENAMES=test_layer&COUNT=1{extra}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("name");
        body.Should().NotContain("category");
    }

    [IntegrationTheory]
    [InlineData("GetFeature", "PROPERTYNAME=category")]
    [InlineData("GetFeature", "SORTBY=category%20A")]
    [InlineData("GetPropertyValue", "VALUEREFERENCE=category")]
    [InlineData("GetFeature", "FILTER=%3Cfes%3AFilter%20xmlns%3Afes%3D%22http%3A%2F%2Fwww.opengis.net%2Ffes%2F2.0%22%3E%3Cfes%3APropertyIsNull%3E%3Cfes%3AValueReference%3Ecategory%3C%2Ffes%3AValueReference%3E%3C%2Ffes%3APropertyIsNull%3E%3C%2Ffes%3AFilter%3E")]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
    public async Task Query_RejectsHiddenFields(string operation, string query)
    {
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST={operation}&TYPENAMES=test_layer&{query}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }
}
