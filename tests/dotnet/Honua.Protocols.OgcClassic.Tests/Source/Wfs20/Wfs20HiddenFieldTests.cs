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

public sealed class Wfs20HiddenFieldTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "category",
            Type = MetadataV2FieldType.String,
            Hidden = true
        });
    }

    public Task DisposeAsync() => App.DisposeAsync();

}

[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Query)]
public sealed class Wfs20HiddenFieldTests : IClassFixture<Wfs20HiddenFieldTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public Wfs20HiddenFieldTests(Wfs20HiddenFieldTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

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
    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_HiddenFieldRemainsWritable()
    {
        const string body = """
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:Update typeName="test_layer">
                <wfs:Property>
                  <wfs:ValueReference>category</wfs:ValueReference>
                  <wfs:Value>private-update-5614</wfs:Value>
                </wfs:Property>
                <fes:Filter><fes:ResourceId rid="test_layer.1" /></fes:Filter>
              </wfs:Update>
            </wfs:Transaction>
            """;
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        payload.Should().Contain("<wfs:totalUpdated>1</wfs:totalUpdated>");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT attributes->>'category' FROM features WHERE objectid = 1 AND layer_id = 0";
        (await command.ExecuteScalarAsync()).Should().Be("private-update-5614");
    }

}
