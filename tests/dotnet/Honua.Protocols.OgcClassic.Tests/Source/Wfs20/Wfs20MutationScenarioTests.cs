// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Certification-depth WFS-T scenarios with a fresh schema for every case.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20MutationScenarioTests : IAsyncLifetime
{
    private string _typeName = "pub-layer-0";
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("pub-layer-0")]
    [InlineData("roads:primary")]
    [InlineData("roads.v2")]
    [Operation(Operations.Create, Operations.Update, Operations.Delete)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_InsertUpdateReplaceDelete_RoundTripsEachState(string publicationId)
    {
        ConfigurePublicationIdentity(publicationId);
        var insert = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Insert handle="mutation-insert">
                <honua:{{_typeName}}>
                  <honua:name>wfs-mutation-created</honua:name>
                  <honua:shape>
                    <gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                      <gml:pos>37.77 -122.41</gml:pos>
                    </gml:Point>
                  </honua:shape>
                </honua:{{_typeName}}>
              </wfs:Insert>
            </wfs:Transaction>
            """;

        var insertBody = await SendTransactionAsync(insert, HttpStatusCode.OK);
        insertBody.Should().Contain("<wfs:totalInserted>1</wfs:totalInserted>");
        var resourceMatch = Regex.Match(
            insertBody,
            $"rid=\"(?<rid>{Regex.Escape(_typeName)}\\.\\d+)\"",
            RegexOptions.CultureInvariant);
        resourceMatch.Success.Should().BeTrue(insertBody);
        var resourceId = resourceMatch.Groups["rid"].Value;
        var numericId = resourceId[(resourceId.LastIndexOf('.') + 1)..];
        (await ReadFeatureAsync(resourceId)).Should().Contain("wfs-mutation-created");

        var update = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:Update typeName="{{_typeName}}">
                <wfs:Property>
                  <wfs:ValueReference>name</wfs:ValueReference>
                  <wfs:Value>wfs-mutation-updated</wfs:Value>
                </wfs:Property>
                <fes:Filter><fes:ResourceId rid="{{resourceId}}" /></fes:Filter>
              </wfs:Update>
            </wfs:Transaction>
            """;
        (await SendTransactionAsync(update, HttpStatusCode.OK))
            .Should().Contain("<wfs:totalUpdated>1</wfs:totalUpdated>");
        (await ReadFeatureAsync(resourceId)).Should().Contain("wfs-mutation-updated");

        var replace = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Replace>
                <honua:{{_typeName}} gml:id="{{resourceId}}">
                  <gml:name>wfs-mutation-replaced</gml:name>
                  <honua:name>wfs-mutation-replaced</honua:name>
                </honua:{{_typeName}}>
                <fes:Filter><fes:ResourceId rid="{{resourceId}}" /></fes:Filter>
              </wfs:Replace>
            </wfs:Transaction>
            """;
        (await SendTransactionAsync(replace, HttpStatusCode.OK))
            .Should().Contain("<wfs:totalReplaced>1</wfs:totalReplaced>");
        (await ReadFeatureAsync(resourceId)).Should().Contain("wfs-mutation-replaced");

        await AssertStoredQueryIdentityAsync(resourceId);

        var delete = $$"""
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:Delete typeName="{{_typeName}}">
                <fes:Filter><fes:ResourceId rid="{{resourceId}}" /></fes:Filter>
              </wfs:Delete>
            </wfs:Transaction>
            """;
        (await SendTransactionAsync(delete, HttpStatusCode.OK))
            .Should().Contain("<wfs:totalDeleted>1</wfs:totalDeleted>");

        var deletedResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID=urn:ogc:def:query:OGC-WFS::GetFeatureById&ID={_typeName}.{numericId}");
        var deletedBody = await deletedResponse.Content.ReadAsStringAsync();
        deletedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, deletedBody);
        deletedBody.Should().Contain("exceptionCode=\"NotFound\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_InvalidInsertAndDelete_AreRejectedWithoutChangingStoredState()
    {
        var existingId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "wfs-mutation-original");
        const string invalidInsert = """
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Insert>
                <honua:pub-layer-0>
                  <honua:name>wfs-invalid-insert</honua:name>
                  <honua:shape>
                    <gml:Point srsName="not-a-crs"><gml:pos>37 -122</gml:pos></gml:Point>
                  </honua:shape>
                </honua:pub-layer-0>
              </wfs:Insert>
            </wfs:Transaction>
            """;
        (await SendTransactionAsync(invalidInsert, HttpStatusCode.BadRequest))
            .Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        (await ReadAllFeaturesAsync()).Should().NotContain("wfs-invalid-insert");

        const string invalidDelete = """
            <wfs:Transaction service="WFS" version="2.0.0"
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:Delete typeName="pub-layer-0"><fes:Filter/></wfs:Delete>
            </wfs:Transaction>
            """;
        (await SendTransactionAsync(invalidDelete, HttpStatusCode.BadRequest))
            .Should().Contain("ExceptionReport");
        (await ReadFeatureAsync($"pub-layer-0.{existingId}"))
            .Should().Contain("wfs-mutation-original");
    }

    private void ConfigurePublicationIdentity(string publicationId)
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var publication = graph.Publications.Single(candidate => candidate.Metadata.Id == "pub-layer-0");
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Publications = graph.Publications.Select(candidate => candidate == publication
                ? candidate with { Metadata = candidate.Metadata with { Id = publicationId, Name = publicationId } }
                : candidate).ToArray(),
        }, schema: _fixture.MetadataGraphSchema);
        _typeName = XmlConvert.EncodeLocalName(publicationId);
    }

    private async Task AssertStoredQueryIdentityAsync(string resourceId)
    {
        using var storedQuery = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID=urn:ogc:def:query:OGC-WFS::GetFeatureById&ID={Uri.EscapeDataString(resourceId)}");
        storedQuery.StatusCode.Should().Be(HttpStatusCode.OK, await storedQuery.Content.ReadAsStringAsync());

        using var wrongCase = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&STOREDQUERY_ID=urn:ogc:def:query:OGC-WFS::GetFeatureById&ID={Uri.EscapeDataString(resourceId.ToUpperInvariant())}");
        wrongCase.StatusCode.Should().Be(HttpStatusCode.NotFound);

    }

    private async Task<string> SendTransactionAsync(string xml, HttpStatusCode expectedStatus)
    {
        using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        var response = await _fixture.Client.PostAsync("/wfs", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, body);
        return body;
    }

    private async Task<string> ReadFeatureAsync(string resourceId)
    {
        var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={_typeName}&RESOURCEID={Uri.EscapeDataString(resourceId)}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    private async Task<string> ReadAllFeaturesAsync()
    {
        var response = await _fixture.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=pub-layer-0");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }
}
