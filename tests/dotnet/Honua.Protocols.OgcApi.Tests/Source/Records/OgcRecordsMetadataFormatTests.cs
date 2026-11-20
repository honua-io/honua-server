// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

// A record item is served as ISO 19139, ISO 19115-3 and DCAT-AP from the record's resolved descriptive
// metadata, chosen by f= or Accept; hostile metadata text stays escaped in every encoding.
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsMetadataFormatTests : IAsyncLifetime
{
    private const string HostileTitle = "Trails </gco:CharacterString><x/> & \"q\" <script>";
    private static readonly string RecordPath =
        $"/ogc/records/collections/honua-catalog/items/{Uri.EscapeDataString($"layer:pub-layer-{WebAppFixture.TestLayerId}")}";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Title = HostileTitle, License = "CC-BY-4.0" });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    [InlineData("iso19139", "application/vnd.iso.19139+xml")]
    [InlineData("iso19115-3", "application/vnd.iso.19115-3+xml")]
    public async Task GetRecord_IsoFormat_EqualsTheWriterOutputForTheResolvedRecord(string format, string mediaType)
    {
        var response = await _fixture.Client.GetAsync($"{RecordPath}?f={format}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(mediaType);
        var served = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var resolved = ResolveDataset();
        var expected = format == "iso19139" ? Iso19139Writer.Write(resolved) : Iso19115Part3Writer.Write(resolved);
        XNode.DeepEquals(served.Root, expected.Root).Should().BeTrue();
        served.Descendants().Should().Contain(element => element.Value == HostileTitle, "hostile text is escaped, not parsed");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetRecord_AcceptDcat_ServesJsonLdWithTheRecordIdentity()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RecordPath);
        request.Headers.Accept.ParseAdd("application/ld+json");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/ld+json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("@type").GetString().Should().Be("dcat:Dataset");
        json.RootElement.GetProperty("dct:title").GetString().Should().Be(HostileTitle);
        json.RootElement.GetProperty("dct:identifier").GetString().Should().Be(ResolveDataset().Identifier);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetRecord_GeoJson_LinksEachMetadataEncoding()
    {
        using var json = JsonDocument.Parse(await _fixture.Client.GetStringAsync(RecordPath));

        var alternates = json.RootElement.GetProperty("links").EnumerateArray()
            .Where(link => link.GetProperty("rel").GetString() == "alternate")
            .Select(link => link.GetProperty("type").GetString())
            .ToList();
        alternates.Should().Contain(["application/vnd.iso.19139+xml", "application/vnd.iso.19115-3+xml", "application/ld+json"]);
    }

    [IntegrationTheory]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    [InlineData("text/csv", HttpStatusCode.NotAcceptable)]
    [InlineData("text/html, */*;q=0.8", HttpStatusCode.OK)]
    public async Task GetRecord_Accept_ServesAnAvailableTypeOrIsNotAcceptable(string accept, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RecordPath);
        request.Headers.TryAddWithoutValidation("Accept", accept);

        (await _fixture.Client.SendAsync(request)).StatusCode.Should().Be(expected);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetRecord_UnknownFormatOrIsoForAService_IsRefused()
    {
        var unknown = await _fixture.Client.GetAsync($"{RecordPath}?f=marc21");
        var serviceRecord = _fixture.GetCurrentV2GraphSnapshot().Graph.Services[0].Metadata.Name;
        var isoForService = await _fixture.Client.GetAsync(
            $"/ogc/records/collections/honua-catalog/items/{Uri.EscapeDataString($"service:{serviceRecord}")}?f=iso19139");

        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        isoForService.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetRecord_ServiceRecordPreferringIso_FallsBackToDcat()
    {
        var serviceRecord = _fixture.GetCurrentV2GraphSnapshot().Graph.Services[0].Metadata.Name;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/ogc/records/collections/honua-catalog/items/{Uri.EscapeDataString($"service:{serviceRecord}")}");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.iso.19139+xml, application/ld+json;q=0.5");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/ld+json");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetRecord_TitleWithAControlCharacter_IsStillValidIso()
    {
        _fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Title = "Roads\u0001 2026" });

        var response = await _fixture.Client.GetAsync($"{RecordPath}?f=iso19139");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants()
            .Should().Contain(element => element.Value == "Roads\uFFFD 2026");
    }

    private DescriptiveMetadata ResolveDataset()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.Single(candidate => candidate.Metadata.Id == $"pub-layer-{WebAppFixture.TestLayerId}");
        var service = snapshot.Graph.Services.SingleOrDefault(candidate => candidate.Metadata.Id == publication.ServiceId);
        return DescriptiveMetadataResolver.ForDataset(
            snapshot.Graph, new DescriptiveMetadataResolver.DatasetContext(snapshot.ResolveResource(publication)!, service, publication));
    }
}
