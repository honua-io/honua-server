// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Tiles;

public sealed class VectorTileHiddenFieldTestsFixture : IAsyncLifetime
{
    public const string HiddenField = "category";

    public WebAppFixture App { get; } = new();

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, new MetadataV2Field
        {
            Name = HiddenField,
            Type = MetadataV2FieldType.String,
            Hidden = true
        });
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

[Collection("Database")]
[Protocol(TestProtocols.Mvt)]
public sealed class VectorTileHiddenFieldTests : IClassFixture<VectorTileHiddenFieldTestsFixture>
{
    private const string MvtMediaType = "application/vnd.mapbox-vector-tile";
    private const string VisibleField = "name";
    private const string HiddenValue = "sample";
    private readonly WebAppFixture _fixture;

    public VectorTileHiddenFieldTests(VectorTileHiddenFieldTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTheory]
    [InlineData("/tiles/0/0/0/0.mvt")]
    [InlineData("/rest/services/test/VectorTileServer/tile/0/0/0.pbf")]
    [InlineData("/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0")]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task ServedTile_OmitsHiddenField(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MvtMediaType));

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the world tile holds the seeded features");
        response.Content.Headers.ContentType!.MediaType.Should().Be(MvtMediaType);
        // MVT attribute keys are stored as plain UTF-8 strings in the layer's key table.
        var tile = Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync());
        tile.Should().Contain(VisibleField, "visible attributes are still served");
        tile.Should().NotContain(VectorTileHiddenFieldTestsFixture.HiddenField);
        tile.Should().NotContain(HiddenValue, "the seeded rows hold this value only in the hidden field");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /tiles/{layerId}/tile.json")]
    public async Task TileJson_DoesNotListHiddenField()
    {
        using var response = await _fixture.Client.GetAsync("/tiles/0/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = document.RootElement.GetProperty("vector_layers")[0].GetProperty("fields")
            .EnumerateObject().Select(field => field.Name).ToArray();
        fields.Should().Contain(VisibleField).And.NotContain(VectorTileHiddenFieldTestsFixture.HiddenField);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task ServedTile_FilterOnHiddenField_IsRefused()
    {
        var where = Uri.EscapeDataString($"{VectorTileHiddenFieldTestsFixture.HiddenField} IS NOT NULL");

        using var response = await _fixture.Client.GetAsync($"/tiles/0/0/0/0.mvt?where={where}");

        response.Content.Headers.ContentType?.MediaType.Should().NotBe(MvtMediaType);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not available").And.NotContain(VectorTileHiddenFieldTestsFixture.HiddenField);
    }
}
