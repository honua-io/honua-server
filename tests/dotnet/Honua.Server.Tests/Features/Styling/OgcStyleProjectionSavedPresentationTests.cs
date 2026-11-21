// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Styling;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using NSubstitute;

namespace Honua.Server.Tests.Features.Styling;

// A layer's saved presentation (the Console drawingInfo override) reaches the vector style
// documents, which the OGC Styles stylesheet, the collection style link and the VectorTileServer
// root.json all read, ahead of the stored MapLibre style, as it already does the raster outputs.
[Trait("Component", "Styling")]
public sealed class OgcStyleProjectionSavedPresentationTests
{
    private const int StorageLayerId = 8377;
    private const string StoredColor = "#123456";
    private const string SavedDrawingInfo =
        """{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[240,170,0,255],"size":10}}}""";

    private readonly ILayerStyleCatalog _styleCatalog = Substitute.For<ILayerStyleCatalog>();

    public OgcStyleProjectionSavedPresentationTests()
    {
        _styleCatalog.GetLayerStyleAsync(StorageLayerId, Arg.Any<CancellationToken>()).Returns(new LayerStyleDefinition
        {
            LayerId = StorageLayerId,
            MapLibreStyleJson = $$$"""{"version":8,"sources":{},"layers":[{"id":"p","type":"circle","source":"s","source-layer":"layer","paint":{"circle-color":"{{{StoredColor}}}"}}]}""",
        });
    }

    [UnitTest]
    public async Task GetStylesheet_LayerWithSavedDrawingInfo_ServesTheSavedPresentation()
    {
        var stylesheet = await Projection(SavedDrawingInfo).GetStylesheetAsync("bng_points", OgcStyleEncoding.MapboxStyle);

        stylesheet.Should().NotBeNull();
        stylesheet!.Content.Should().NotContain(StoredColor);
        CircleColors(stylesheet.Content).Should().ContainSingle().Which.Should().MatchRegex("(?i)(240.*170.*0|#f0aa00)");
    }

    [UnitTest]
    public async Task GetAssociatedStylesheet_LayerWithSavedDrawingInfo_ServesTheSavedPresentation()
    {
        var stylesheet = await Projection(SavedDrawingInfo).GetAssociatedStylesheetAsync("resource-bng", "bng_points", OgcStyleEncoding.MapboxStyle);

        stylesheet!.Content.Should().NotContain(StoredColor);
    }

    [UnitTest]
    public async Task GetStylesheet_EsriEncoding_ServesTheSavedDrawingInfoAsSaved()
    {
        var stylesheet = await Projection(SavedDrawingInfo).GetStylesheetAsync("bng_points", OgcStyleEncoding.GeoServicesDrawingInfo);

        JsonElement.Parse(stylesheet!.Content).GetProperty("renderer").GetProperty("symbol").GetProperty("color")[0].GetInt32().Should().Be(240);
    }

    [UnitTest]
    public async Task GetStylesheet_LayerWithoutSavedDrawingInfo_KeepsTheStoredStyle()
    {
        var stylesheet = await Projection(drawingInfo: null).GetStylesheetAsync("bng_points", OgcStyleEncoding.MapboxStyle);

        stylesheet!.Content.Should().Contain(StoredColor);
    }

    [UnitTest]
    public async Task UpdateStyle_LayerStyle_ReportsTheStorageLayerForEviction()
    {
        var styles = Substitute.For<ILayerStyleService>();
        styles.UpdateStyleAsync(
                Arg.Any<MetadataV2Object>(), StorageLayerId, Arg.Any<JsonElement?>(), Arg.Any<JsonElement?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new LayerStyleUpdateResult(LayerStyleUpdateStatus.Updated, null, null));

        var result = await Projection(SavedDrawingInfo, styles)
            .UpdateStyleAsync("bng_points", """{"version":8,"sources":{},"layers":[]}""", new OgcStyleWriteOptions());

        result.Status.Should().Be(OgcStyleUpdateStatus.Updated);
        result.StorageLayerId.Should().Be(StorageLayerId);
    }

    private OgcStyleProjection Projection(string? drawingInfo, ILayerStyleService? styles = null)
    {
        var extensions = drawingInfo is null
            ? null
            : new Dictionary<string, JsonElement> { ["geoservices:drawingInfo"] = JsonElement.Parse(drawingInfo) };
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource(
                "resource-bng",
                "bng_points",
                spatial: new MetadataV2ObjectSpatial { GeometryType = MetadataV2GeometryType.Point },
                extensions: extensions)
            .AddStorageBinding("binding-bng", "resource-bng", "public.bng_points", storageLayerId: StorageLayerId)
            .BuildProvider();
        return new OgcStyleProjection(graph, styles ?? Substitute.For<ILayerStyleService>(), _styleCatalog, new GeoServicesStyleConverter());
    }

    private static List<string> CircleColors(string mapLibreJson)
    {
        using var document = JsonDocument.Parse(mapLibreJson);
        return document.RootElement.GetProperty("layers").EnumerateArray()
            .Where(layer => layer.TryGetProperty("paint", out var paint) && paint.TryGetProperty("circle-color", out _))
            .Select(layer => layer.GetProperty("paint").GetProperty("circle-color").ToString())
            .ToList();
    }
}
