// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsDescriptiveMetadataTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task GetRecords_PublicationTitleOverride_IsSearchable()
    {
        _fixture.MutateV2Graph(graph => graph with
        {
            Publications = graph.Publications.Select(publication => publication with
            {
                Metadata = publication.Metadata with { Title = "DistinctivePublicationTitle", Description = "DistinctivePublicationDescription" },
            }).ToArray(),
        });

        foreach (var query in new[] { "DistinctivePublicationTitle", "DistinctivePublicationDescription" })
        {
            var response = await _fixture.Client.GetAsync($"/ogc/records/collections/honua-catalog/items?q={query}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("features").EnumerateArray().Should()
                .Contain(feature => feature.GetProperty("properties").GetProperty("title").GetString() == "DistinctivePublicationTitle");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items/{recordId}")]
    public async Task GetRecord_EditedDescriptiveMetadata_AppearsInRecordProperties()
    {
        _fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with
        {
            Title = "County trails",
            Description = "Maintained trail centerlines.",
            Keywords = ["trails"],
            Themes = ["TRAN"],
            Language = "en",
            License = "CC-BY-4.0",
            AccessConstraints = "Public, no restrictions.",
            Contacts = [new MetadataV2ResponsibleParty { Role = "pointOfContact", Name = "Ana Ruiz", Email = "gis@example.org", Url = "https://example.org/contact" }],
        });

        var recordId = Uri.EscapeDataString($"layer:pub-layer-{WebAppFixture.TestLayerId}");
        var response = await _fixture.Client.GetAsync($"/ogc/records/collections/honua-catalog/items/{recordId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = json.RootElement.GetProperty("properties");
        properties.GetProperty("title").GetString().Should().Be("County trails");
        properties.GetProperty("keywords")[0].GetString().Should().Be("trails");
        properties.GetProperty("themes")[0].GetProperty("concepts")[0].GetProperty("id").GetString().Should().Be("TRAN");
        properties.GetProperty("language").GetProperty("code").GetString().Should().Be("en");
        properties.GetProperty("license").GetString().Should().Be("CC-BY-4.0");
        properties.GetProperty("rights").GetString().Should().Be("Public, no restrictions.");
        var contact = properties.GetProperty("contacts")[0];
        contact.GetProperty("emails")[0].GetProperty("value").GetString().Should().Be("gis@example.org");
        contact.GetProperty("roles")[0].GetString().Should().Be("pointOfContact");
        var link = contact.GetProperty("links")[0];
        link.GetProperty("href").GetString().Should().Be("https://example.org/contact");
        link.GetProperty("type").GetString().Should().Be("text/html");
    }
}
