// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

// The layer's catalog style classifies on "notes", which the caller's mask withholds; legends list none of its classes.
[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class GeoServicesWithheldFieldLegendTests(GeoServicesWithheldFieldLegendFixture fixture)
    : IClassFixture<GeoServicesWithheldFieldLegendFixture>
{
    private const int SwatchSize = 20;

    private readonly WebAppFixture _app = fixture.App;

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetLegendGraphic")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task WmsGetLegendGraphic_DrawsOnlyTheFallbackRowForAMaskedClassField()
    {
        using var response = await _app.Client.GetAsync(
            "/rest/services/test/MapServer/WMS?SERVICE=WMS&REQUEST=GetLegendGraphic&VERSION=1.3.0" +
            $"&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png&WIDTH={SwatchSize}&HEIGHT={SwatchSize}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());

        bitmap.Height.Should().Be(LegendHeight(rows: 1), "the 'Confidential' class row is not drawn, only the fallback");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    public async Task MapServerLegend_ListsNoClassOfAMaskedCatalogStyleField()
    {
        using var response = await _app.Client.GetAsync("/rest/services/test/MapServer/legend?f=json");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("layers")[0].GetProperty("legend").GetArrayLength().Should().Be(1);
        body.Should().NotContain("Confidential");
    }

    private static int LegendHeight(int rows) => LegendImageComposer.Measure(
        [.. Enumerable.Range(0, rows).Select(_ => new LegendImageComposer.LegendImageEntry(
            new MapLibreStyleLayer { Type = "circle" },
            new LegendClass("row", ImmutableDictionary<string, object?>.Empty)))],
        SwatchSize,
        SwatchSize).Height;
}

public sealed class GeoServicesWithheldFieldLegendFixture : IAsyncLifetime
{
    private const string NotesClassStyle = """
        {"version":8,"layers":[{"id":"notes-classes","type":"circle","paint":{
          "circle-radius":6,
          "circle-color":["match",["get","notes"],"Confidential","#d7191c","#2c7bb6"]}}]}
        """;

    public WebAppFixture App { get; } = new WebAppFixture()
        .ReplaceService<IFieldMaskSource>(new NotesFieldMaskSource());

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
        await App.GetService<ILayerStyleCatalog>().SetMapLibreStyleAsync(WebAppFixture.TestLayerId, NotesClassStyle);
    }

    public Task DisposeAsync() => App.DisposeAsync();

    private sealed class NotesFieldMaskSource : IFieldMaskSource
    {
        public Task<ImmutableArray<string>> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableArray.Create("notes"));
    }
}
