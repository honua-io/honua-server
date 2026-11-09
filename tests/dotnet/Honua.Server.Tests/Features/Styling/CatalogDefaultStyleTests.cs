// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
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
        await layerStyles.SetMapLibreStyleAsync(LayerId, CircleStyle("#0000ff"));
        (await layerStyles.GetLayerStyleAsync(LayerId))!.MapLibreStyleJson.Should().Contain("#0000ff");
        var catalog = _fixture.GetService<IStyleCatalog>();
        var styleId = await AssociateStyleAsync("#ff0000");

        var promoted = await layerStyles.GetLayerStyleAsync(LayerId);
        promoted!.MapLibreStyleJson.Should().Contain("#ff0000");
        await catalog.UpdateStyleAsync(styleId, CircleStyle("#00ff00"), revisedBy: "operator");
        var edited = await layerStyles.GetLayerStyleAsync(LayerId);

        edited!.MapLibreStyleJson.Should().Contain("#00ff00");
        edited.StyleVersion.Should().Be(promoted.StyleVersion + 1);
        edited.StyleRevisedBy.Should().Be("operator");
        await catalog.DeleteStyleAsync(styleId);
        (await layerStyles.GetLayerStyleAsync(LayerId))!.MapLibreStyleJson.Should().Contain("#0000ff");
    }

    [IntegrationTheory]
    [InlineData("GetMap", "LAYERS=1&STYLES=&CRS=CRS:84&BBOX=-123,37,-122,38&WIDTH=256&HEIGHT=256")]
    [InlineData("GetLegendGraphic", "LAYER=1&STYLE=default")]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [InterfaceOperation(TestProtocols.Wms13, "GetLegendGraphic")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task Wms_CatalogDefault_RendersCatalogColor(string operation, string parameters)
    {
        await _fixture.GetService<ILayerStyleCatalog>().SetMapLibreStyleAsync(LayerId, CircleStyle("#0000ff"));
        await AssociateStyleAsync("#ff0000");

        using var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wms?SERVICE=WMS&VERSION=1.3.0"
            + $"&REQUEST={operation}&FORMAT=image/png&{parameters}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        bitmap.Should().NotBeNull();
        bitmap.Pixels.Should().Contain(pixel => pixel.Red > 200 && pixel.Green < 40 && pixel.Blue < 40);
        bitmap.Pixels.Should().NotContain(pixel => pixel.Blue > 200 && pixel.Red < 40 && pixel.Green < 40);
    }

    private async Task<string> AssociateStyleAsync(string color)
    {
        var catalog = _fixture.GetService<IStyleCatalog>();
        var styleId = $"default-{Guid.NewGuid():N}";
        await catalog.CreateStyleAsync(styleId, CircleStyle(color));
        (await catalog.AssociateLayerAsync(LayerId, styleId)).Should().BeTrue();
        return styleId;
    }

    private static string CircleStyle(string color) => $$"""
        {"version":8,"sources":{},"layers":[{"id":"points","type":"circle","source":"features",
        "paint":{"circle-color":"{{color}}","circle-radius":10}}]}
        """;
}
