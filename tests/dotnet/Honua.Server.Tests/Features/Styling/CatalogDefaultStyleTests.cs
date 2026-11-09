// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Styling;

[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class CatalogDefaultStyleTests : IAsyncLifetime
{
    private const int LayerId = 1;
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task GetLayerStyleAsync_CatalogPromotionAndEdit_ReplaceCachedLayerStyle()
    {
        var layerStyles = _fixture.GetService<ILayerStyleCatalog>();
        await layerStyles.SetStyleAsync(LayerId, CircleStyle("#0000ff"), "{}");
        (await layerStyles.GetLayerStyleAsync(LayerId))!.MapLibreStyleJson.Should().Contain("#0000ff");
        var catalog = _fixture.GetService<IStyleCatalog>();
        var styleId = await AssociateStyleAsync("#ff0000");

        var promoted = await layerStyles.GetLayerStyleAsync(LayerId);
        promoted!.MapLibreStyleJson.Should().Contain("#ff0000");
        promoted.DrawingInfoJson.Should().BeNull();
        await catalog.UpdateStyleAsync(styleId, CircleStyle("#00ff00"), revisedBy: "operator");
        var edited = await layerStyles.GetLayerStyleAsync(LayerId);

        edited!.MapLibreStyleJson.Should().Contain("#00ff00");
        edited.StyleVersion.Should().Be(promoted.StyleVersion + 1);
        edited.StyleRevisedBy.Should().Be("operator");
        await catalog.DeleteStyleAsync(styleId);
        (await layerStyles.GetLayerStyleAsync(LayerId))!.MapLibreStyleJson.Should().Contain("#0000ff");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetLayerStyleAsync_AlternateAssociation_DoesNotReplaceLayerFallback()
    {
        var layerStyles = _fixture.GetService<ILayerStyleCatalog>();
        await layerStyles.SetMapLibreStyleAsync(LayerId, CircleStyle("#0000ff"));
        var catalog = _fixture.GetService<IStyleCatalog>();
        var styleId = $"alternate-{Guid.NewGuid():N}";
        await catalog.CreateStyleAsync(styleId, CircleStyle("#ff0000"));
        (await catalog.AssociateLayerAsync(LayerId, styleId, ordinal: 1)).Should().BeTrue();

        (await layerStyles.GetLayerStyleAsync(LayerId))!.MapLibreStyleJson.Should().Contain("#0000ff");
        (await layerStyles.GetLayerStyleAsync(int.MaxValue)).Should().BeNull();
    }

    [IntegrationTheory]
    [InlineData("/ogc/services/test/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&FORMAT=image/png&LAYERS=1&STYLES=&CRS=CRS:84&BBOX=-123,37,-122,38&WIDTH=256&HEIGHT=256")]
    [InlineData("/ogc/services/test/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetLegendGraphic&FORMAT=image/png&LAYER=1&STYLE=default")]
    [InlineData("/ogc/services/test/wmts?SERVICE=WMTS&VERSION=1.0.0&REQUEST=GetTile&FORMAT=image/png&LAYER=1&STYLE=default&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0")]
    [InlineData("/static/test/bbox/-123,37,-122,38/256x256.png?layers=1")]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [InterfaceOperation(TestProtocols.Wms13, "GetLegendGraphic")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    [Endpoint("GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}")]
    public async Task Render_CatalogPromotionAndEdit_UsesCurrentCatalogColor(string path)
    {
        await _fixture.GetService<ILayerStyleCatalog>().SetMapLibreStyleAsync(LayerId, CircleStyle("#0000ff"));
        using var initial = await _fixture.Client.GetAsync(path);
        await AssertColorAsync(initial, SKColors.Blue);
        var styleId = await AssociateStyleAsync("#ff0000");

        using var promoted = await _fixture.Client.GetAsync(path);
        await AssertColorAsync(promoted, SKColors.Red);
        await _fixture.GetService<IStyleCatalog>().UpdateStyleAsync(styleId, CircleStyle("#00ff00"));
        using var edited = await _fixture.Client.GetAsync(path);
        await AssertColorAsync(edited, SKColors.Lime);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.PrintingTools)]
    [Operation(Operations.Print)]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute")]
    public async Task Print_CatalogDefault_RendersCatalogColor()
    {
        await _fixture.GetService<ILayerStyleCatalog>().SetMapLibreStyleAsync(LayerId, CircleStyle("#0000ff"));
        await AssociateStyleAsync("#ff0000");
        var webMap = JsonSerializer.Serialize(new
        {
            mapOptions = new
            {
                extent = new
                {
                    xmin = -123, ymin = 37, xmax = -122, ymax = 38,
                    spatialReference = new { wkid = 4326 }
                }
            },
            operationalLayers = new[] { new { url = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/1" } },
            exportOptions = new { dpi = 96, outputSize = new[] { 256, 256 } }
        });
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Web_Map_as_JSON"] = webMap, ["Format"] = "PNG32", ["Layout_Template"] = "MAP_ONLY"
        });

        using var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task/execute", form);

        await AssertColorAsync(response, SKColors.Red);
    }

    private static async Task AssertColorAsync(HttpResponseMessage response, SKColor color)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        bitmap.Should().NotBeNull();
        bitmap.Pixels.Should().Contain(pixel => pixel.Red == color.Red && pixel.Green == color.Green && pixel.Blue == color.Blue);
    }

    private async Task<string> AssociateStyleAsync(string color)
    {
        var catalog = _fixture.GetService<IStyleCatalog>();
        var styleId = $"default-{Guid.NewGuid():N}";
        await catalog.CreateStyleAsync(styleId, CircleStyle(color));
        (await catalog.AssociateLayerAsync(LayerId, styleId)).Should().BeTrue();
        return styleId;
    }

    private static string CircleStyle(string color) => $$$"""
        {"version":8,"sources":{},"layers":[{"id":"points","type":"circle","source":"features",
        "paint":{"circle-color":"{{{color}}}","circle-radius":10}}]}
        """;
}
