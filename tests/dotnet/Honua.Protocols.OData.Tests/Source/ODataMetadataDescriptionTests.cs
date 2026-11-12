// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataMetadataDescriptionTests : IAsyncLifetime
{
    private static readonly XNamespace Edm = "http://docs.oasis-open.org/odata/ns/edm";
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_EditedCatalogTitleAndDescription_AnnotateTheContainer()
    {
        _fixture.MutateV2Graph(graph => graph with
        {
            Metadata = graph.Metadata with { Title = "County open data", Description = "Parcels & trails, refreshed nightly." },
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/$metadata");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var annotations = document.Descendants(Edm + "Annotations")
            .Single(element => element.Attribute("Target")?.Value == "Honua.Container")
            .Elements(Edm + "Annotation")
            .ToDictionary(element => element.Attribute("Term")!.Value, element => element.Attribute("String")!.Value);
        annotations["Core.Description"].Should().Be("County open data");
        annotations["Core.LongDescription"].Should().Be("Parcels & trails, refreshed nightly.");
    }
}
