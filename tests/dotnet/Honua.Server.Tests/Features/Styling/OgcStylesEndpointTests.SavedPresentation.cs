// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Styling;

// An OGC Styles write retires the layer's saved presentation (the Console drawingInfo override),
// and the layer's own service metadata stops advertising it, as after the admin style endpoints.
public sealed partial class OgcStylesEndpointTests
{
    private const string SavedPresentation =
        """{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[240,170,0,255],"size":10}}}""";

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/drawing-info")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task PutStyle_LayerWithSavedPresentation_RetiresItInStylesAndMapServerMetadata()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);
        using (var saved = new StringContent(SavedPresentation, Encoding.UTF8, "application/json"))
        {
            (await client.PutAsync($"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/drawing-info", saved)).Be200Ok();
        }

        (await MapServerSymbolRedAsync(client)).Should().Be(240, "the saved presentation is advertised before the style write");

        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        (await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content)).StatusCode
            .Should().Be(System.Net.HttpStatusCode.NoContent);

        (await MapServerSymbolRedAsync(client)).Should().NotBe(240);
        (await client.GetStringAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}"))
            .Should().NotMatchRegex(@"(?i)(240\s*,\s*170\s*,\s*0|#f0aa00)");
    }

    private static async Task<int?> MapServerSymbolRedAsync(HttpClient client)
    {
        using var layer = JsonDocument.Parse(await client.GetStringAsync($"/rest/services/test/MapServer/{WebAppFixture.TestLayerId}?f=json"));
        return layer.RootElement.TryGetProperty("drawingInfo", out var drawingInfo)
            && drawingInfo.TryGetProperty("renderer", out var renderer)
            && renderer.TryGetProperty("symbol", out var symbol)
            && symbol.TryGetProperty("color", out var color)
            && color.ValueKind == JsonValueKind.Array
            ? color[0].GetInt32()
            : null;
    }
}
