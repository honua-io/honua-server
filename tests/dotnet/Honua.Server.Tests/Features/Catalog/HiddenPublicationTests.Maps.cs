// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Catalog;

public sealed partial class HiddenPublicationTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiMaps)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/maps/collections")]
    public async Task MapCollections_HiddenSibling_UsesOnlyAdvertisedPublicationMetadata()
    {
        var publication = _original.Publications.First(publication => publication.LayerIndex == WebAppFixture.TestLayerId);
        _provider.SetGraph(_original with
        {
            Revision = _original.Revision + 1,
            Publications = [publication with { Advertised = false, TitleOverride = "Hidden-only title" }, publication with
            {
                Metadata = publication.Metadata with { Id = "advertised-publication" },
                Advertised = true, TitleOverride = "Advertised title"
            }]
        }, schema: _fixture.CurrentSchema);
        using var response = await _fixture.Client.GetAsync("/ogc/maps/collections");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var collection = Assert.Single(document.RootElement.GetProperty("collections").EnumerateArray());
        Assert.Equal("Advertised title", collection.GetProperty("title").GetString());
        Assert.DoesNotContain("Hidden-only title", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }
}
