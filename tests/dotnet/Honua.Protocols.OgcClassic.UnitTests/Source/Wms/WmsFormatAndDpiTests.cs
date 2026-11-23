// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Protocol(TestProtocols.Wms13)]
public sealed class WmsFormatAndDpiTests
{
    private readonly ISqlFilterTranslator _sql = Substitute.For<ISqlFilterTranslator>();

    public WmsFormatAndDpiTests()
    {
        _sql.Translate(Arg.Any<FilterExpression>(), Arg.Any<MetadataV2Object>())
            .Returns(new SqlFragment("population > 100", []));
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void FormatOptionsDpi_ParsesValidAndRejectsInvalidValues()
    {
        WmsRequestHandlers.ParseWmsFormatOptionsDpi("dpi:180").Should().Be((180.0, null));
        WmsRequestHandlers.ParseWmsFormatOptionsDpi("antialias:full;dpi:200.5").Should().Be((200.5, null));
        WmsRequestHandlers.ParseWmsFormatOptionsDpi(null).Should().Be((null, null));

        WmsRequestHandlers.ParseWmsFormatOptionsDpi("dpi:0").Error.Should().NotBeNull();
        WmsRequestHandlers.ParseWmsFormatOptionsDpi("dpi:-90").Error.Should().NotBeNull();
        WmsRequestHandlers.ParseWmsFormatOptionsDpi("dpi:9999").Error.Should().NotBeNull();
        WmsRequestHandlers.ParseWmsFormatOptionsDpi("dpi:abc").Error.Should().NotBeNull();
        WmsRequestHandlers.ParseWmsFormatOptionsDpi("malformed").Error.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void Png8Encoder_ExactPalette_PreservesAlphaAndColor()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.SteelBlue, Style = SKPaintStyle.Fill };
        surface.Canvas.DrawRect(4, 4, 24, 24, paint);

        var png8Bytes = SkiaMapRenderer.EncodeSurface(surface, "png8");
        png8Bytes[0..8].Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        png8Bytes[24].Should().Be(8);
        png8Bytes[25].Should().Be(3);

        using var decoded = SKBitmap.Decode(png8Bytes);
        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(32);
        decoded.GetPixel(0, 0).Alpha.Should().Be(0);
        decoded.GetPixel(16, 16).Should().Be(SKColors.SteelBlue);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void Png8Encoder_QuantizedPalette_DecodesValidIndexedPng()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                using var paint = new SKPaint { Color = new SKColor((byte)(x * 8), (byte)(y * 8), (byte)((x + y) * 4), 255) };
                surface.Canvas.DrawPoint(x, y, paint);
            }
        }

        var quantizedBytes = SkiaMapRenderer.EncodeSurface(surface, "png8");
        quantizedBytes[25].Should().Be(3);
        using var decoded = SKBitmap.Decode(quantizedBytes);
        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(32);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void Png8Encoder_QuantizedPalette_KeepsSemiTransparentFills()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.Transparent);
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                using var paint = new SKPaint { Color = new SKColor((byte)(x * 16), (byte)(y * 8), 40, 255) };
                surface.Canvas.DrawPoint(x, y, paint);
            }
        }

        using var fill = new SKPaint { Color = SKColors.SteelBlue.WithAlpha(102), Style = SKPaintStyle.Fill };
        surface.Canvas.DrawRect(16, 0, 16, 32, fill);

        using var decoded = SKBitmap.Decode(SkiaMapRenderer.EncodeSurface(surface, "png8"));
        decoded.Should().NotBeNull();
        var translucent = decoded!.GetPixel(24, 16);
        translucent.Alpha.Should().BeInRange(86, 118);
        translucent.Blue.Should().BeInRange(164, 196);
        decoded.GetPixel(4, 4).Alpha.Should().Be(255);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void WebpEncoder_ProducesValidDecodableWebp()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.ForestGreen, Style = SKPaintStyle.Fill };
        surface.Canvas.DrawCircle(16, 16, 10, paint);

        var webpBytes = SkiaMapRenderer.EncodeSurface(surface, "webp");
        Encoding.ASCII.GetString(webpBytes, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(webpBytes, 8, 4).Should().Be("WEBP");
        using var decoded = SKBitmap.Decode(webpBytes);
        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(32);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task HandleWms_GetCapabilitiesAndCqlFilter_WorkEndToEnd()
    {
        FeatureQuery? capturedQuery = null;
        var reader = WmsCqlAndHtmlTests.CreateSampleFeatureReader(q => capturedQuery = q);
        await using var sp = WmsCqlAndHtmlTests.BuildWmsServices(WmsCqlAndHtmlTests.CreateResource(), reader, _sql);

        var (capStatus, _, capBytes) = await WmsCqlAndHtmlTests.ExecuteWmsAsync(sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetCapabilities");
        capStatus.Should().Be(StatusCodes.Status200OK);
        Encoding.UTF8.GetString(capBytes).Should()
            .Contain("<Format>image/png8</Format>")
            .And.Contain("<Format>image/webp</Format>")
            .And.Contain("<Format>text/html</Format>");

        var (cqlStatus, _, _) = await WmsCqlAndHtmlTests.ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png&CQL_FILTER=population+%3E+100");
        cqlStatus.Should().Be(StatusCodes.Status200OK);
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.SqlFilter.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task HandleWms_GetMap_SupportsPng8WebpAndDefaultPngJpeg()
    {
        await using var sp = WmsCqlAndHtmlTests.BuildWmsServices(
            WmsCqlAndHtmlTests.CreateResource(), WmsCqlAndHtmlTests.CreateSampleFeatureReader(), _sql);

        var (png8Status, png8Type, png8Bytes) = await WmsCqlAndHtmlTests.ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png8");
        png8Status.Should().Be(StatusCodes.Status200OK);
        png8Type.Should().Be("image/png");
        png8Bytes[25].Should().Be(3);

        var (webpStatus, webpType, webpBytes) = await WmsCqlAndHtmlTests.ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/webp");
        webpStatus.Should().Be(StatusCodes.Status200OK);
        webpType.Should().Be("image/webp");
        Encoding.ASCII.GetString(webpBytes, 8, 4).Should().Be("WEBP");

        var (jpegStatus, jpegType, jpegBytes) = await WmsCqlAndHtmlTests.ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/jpeg");
        jpegStatus.Should().Be(StatusCodes.Status200OK);
        jpegType.Should().Be("image/jpeg");
        jpegBytes[0..2].Should().Equal(0xFF, 0xD8);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task HandleWms_GetMap_DpiScaling_ScalesOutputAndRejectsInvalidDpi()
    {
        await using var sp = WmsCqlAndHtmlTests.BuildWmsServices(
            WmsCqlAndHtmlTests.CreateResource(), WmsCqlAndHtmlTests.CreateSampleFeatureReader(), _sql);

        var (defaultStatus, _, defaultBytes) = await WmsCqlAndHtmlTests.ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png");
        var (dpiStatus, _, dpiBytes) = await WmsCqlAndHtmlTests.ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png&FORMAT_OPTIONS=dpi:270");
        defaultStatus.Should().Be(StatusCodes.Status200OK);
        dpiStatus.Should().Be(StatusCodes.Status200OK);
        CountNonWhitePixels(dpiBytes).Should().BeGreaterThan(CountNonWhitePixels(defaultBytes));

        var (badDpiStatus, _, badDpiBytes) = await WmsCqlAndHtmlTests.ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png&FORMAT_OPTIONS=dpi:notanumber");
        badDpiStatus.Should().Be(StatusCodes.Status400BadRequest);
        Encoding.UTF8.GetString(badDpiBytes).Should().Contain("InvalidParameterValue");
    }

    private static int CountNonWhitePixels(byte[] imageBytes)
    {
        using var bmp = SKBitmap.Decode(imageBytes)!;
        var count = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 250 || c.Green < 250 || c.Blue < 250)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
