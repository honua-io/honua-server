// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

public sealed class VectorAwareRasterMapRendererTests
{
    [UnitTest]
    public async Task RenderCollectionMapAsync_OpaqueBackground_CompositesRasterTransparency()
    {
        var source = CreateTransparentPng();
        var inner = new StubRasterMapRenderer(source);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var renderer = new VectorAwareRasterMapRenderer(
            inner,
            services,
            NullLogger<VectorAwareRasterMapRenderer>.Instance);

        var result = await renderer.RenderCollectionMapAsync(
            1,
            new MapRenderRequest
            {
                BoundingBox = [0d, 0d, 1d, 1d],
                Width = 1,
                Height = 1,
                Format = RasterFormat.PNG,
                Transparent = false,
                BackgroundColor = "0xFF0000"
            });

        using var bitmap = SKBitmap.Decode(result.Data);
        bitmap.Should().NotBeNull();
        bitmap.GetPixel(0, 0).Should().Be(new SKColor(255, 0, 0, 255),
            "accepted OGC map background options must apply to raster-backed PNG output (#4164)");
    }

    private static byte[] CreateTransparentPng()
    {
        using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class StubRasterMapRenderer(byte[] data) : IRasterMapRenderer
    {
        private readonly RasterResult _result = new()
        {
            Data = data,
            ContentType = "image/png",
            Width = 1,
            Height = 1
        };

        public Task<RasterResult> RenderCollectionMapAsync(
            int layerId,
            MapRenderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_result);

        public Task<RasterResult> RenderDatasetMapAsync(
            int[] layerIds,
            MapRenderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_result);

        public Task<RasterResult> RenderStyledMapAsync(
            int layerId,
            string styleId,
            MapRenderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }
}
