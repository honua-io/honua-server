// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Styling;

// An OGC Styles write retires the layer's saved presentation (the Console drawingInfo override)
// and evicts the layer's cached service output, as the endpoint that set the override does.
public sealed partial class OgcStylesEndpointTests
{
    private const string SavedPresentation =
        """{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[240,170,0,255],"size":10}}}""";

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/drawing-info")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task PutStyle_LayerWithSavedPresentation_RetiresItAndEvictsTheLayer()
    {
        var cache = Substitute.For<IOutputCacheStore>();
        await using var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IOutputCacheStore>();
            services.AddSingleton(cache);
        });
        await fixture.InitializeAsync();
        var client = fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);
        var styleId = fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId].Metadata.Name;
        using (var saved = new StringContent(SavedPresentation, Encoding.UTF8, "application/json"))
        {
            (await client.PutAsync($"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/drawing-info", saved)).Be200Ok();
        }

        (await MapServerSymbolRedAsync(client)).Should().Be(240, "the saved presentation is advertised before the style write");
        cache.ClearReceivedCalls();

        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        (await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        await cache.Received().EvictByTagAsync($"layer:{WebAppFixture.TestLayerId}", Arg.Any<CancellationToken>());
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
