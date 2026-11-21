// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Protocol(TestProtocols.Wms13)]
public sealed class WmsCqlFormatsAndHtmlTests
{
    private readonly ISqlFilterTranslator _sql = Substitute.For<ISqlFilterTranslator>();
    private FilterExpression? _translated;

    public WmsCqlFormatsAndHtmlTests()
    {
        _sql.Translate(Arg.Any<FilterExpression>(), Arg.Any<MetadataV2Object>())
            .Returns(call =>
            {
                _translated = call.Arg<FilterExpression>();
                return new SqlFragment("population > 100", []);
            });
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void CqlFilter_ValidPredicate_TranslatesUsingCql2Parser()
    {
        var (filter, error) = TranslateCql("population > 100 AND name = 'Honolulu'");

        error.Should().BeNull();
        filter.Should().NotBeNull();
        var logical = _translated.Should().BeOfType<BinaryExpression>().Subject;
        logical.Operator.Should().Be(BinaryOperator.And);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task CqlFilter_InvalidSyntax_ReturnsInvalidParameterValueException()
    {
        var (status, body) = await TranslateCqlAsync("population >>>");

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("InvalidParameterValue").And.Contain("CQL_FILTER");
        _translated.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task CqlFilter_HiddenOrUnknownField_ReturnsInvalidParameterValueException()
    {
        var (hiddenStatus, hiddenBody) = await TranslateCqlAsync("secret_code = 'abc'");
        hiddenStatus.Should().Be(StatusCodes.Status400BadRequest);
        hiddenBody.Should().Contain("InvalidParameterValue");

        var (unknownStatus, unknownBody) = await TranslateCqlAsync("does_not_exist = 1");
        unknownStatus.Should().Be(StatusCodes.Status400BadRequest);
        unknownBody.Should().Contain("InvalidParameterValue");
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void SplitWmsCqlFilterTokens_PreservesSemicolonsInsideQuotes()
    {
        var tokens = WmsRequestHandlers.SplitWmsCqlFilterTokens("name = 'a;b';population > 10");

        tokens.Should().Equal("name = 'a;b'", "population > 10");
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void FormatOptionsDpi_ParsesValidAndRejectsInvalidValues()
    {
        WmsRequestHandlers.TryParseWmsFormatOptionsDpi("dpi:180", out var dpi, out _).Should().BeTrue();
        dpi.Should().Be(180.0);

        WmsRequestHandlers.TryParseWmsFormatOptionsDpi("antialias:full;dpi:200.5", out var dpi2, out _).Should().BeTrue();
        dpi2.Should().Be(200.5);

        WmsRequestHandlers.TryParseWmsFormatOptionsDpi(null, out var absent, out _).Should().BeTrue();
        absent.Should().BeNull();

        WmsRequestHandlers.TryParseWmsFormatOptionsDpi("dpi:0", out _, out _).Should().BeFalse();
        WmsRequestHandlers.TryParseWmsFormatOptionsDpi("dpi:-90", out _, out _).Should().BeFalse();
        WmsRequestHandlers.TryParseWmsFormatOptionsDpi("dpi:9999", out _, out _).Should().BeFalse();
        WmsRequestHandlers.TryParseWmsFormatOptionsDpi("dpi:abc", out _, out _).Should().BeFalse();
        WmsRequestHandlers.TryParseWmsFormatOptionsDpi("malformed", out _, out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void Png8Encoder_ProducesValidIndexedPng_ForExactAndQuantizedImages()
    {
        // Exact palette path (<= 256 colors) with transparency
        using (var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul))!)
        {
            surface.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.SteelBlue, Style = SKPaintStyle.Fill };
            surface.Canvas.DrawRect(4, 4, 24, 24, paint);

            var png8Bytes = SkiaMapRenderer.EncodeSurface(surface, "png8");
            png8Bytes.Length.Should().BeGreaterThan(33);
            png8Bytes[0..8].Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
            // IHDR bit depth at offset 24, color type at offset 25 (3 = indexed palette)
            png8Bytes[24].Should().Be(8);
            png8Bytes[25].Should().Be(3);

            using var decoded = SKBitmap.Decode(png8Bytes);
            decoded.Should().NotBeNull();
            decoded!.Width.Should().Be(32);
            decoded.Height.Should().Be(32);
            decoded.GetPixel(0, 0).Alpha.Should().Be(0);
            decoded.GetPixel(16, 16).Should().Be(SKColors.SteelBlue);
        }

        // Quantized palette path (> 256 distinct colors)
        using (var gradientSurface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul))!)
        {
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    using var p = new SKPaint { Color = new SKColor((byte)(x * 8), (byte)(y * 8), (byte)((x + y) * 4), 255) };
                    gradientSurface.Canvas.DrawPoint(x, y, p);
                }
            }

            var quantizedBytes = SkiaMapRenderer.EncodeSurface(gradientSurface, "png8");
            quantizedBytes[25].Should().Be(3);
            using var decoded = SKBitmap.Decode(quantizedBytes);
            decoded.Should().NotBeNull();
            decoded!.Width.Should().Be(32);
            decoded.Height.Should().Be(32);
        }
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
        decoded.Height.Should().Be(32);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void PopupHtmlFormatter_RendersPopupTitleDescriptionAndVisibleFields_AndHtmlEncodesValues()
    {
        using var popupDoc = JsonDocument.Parse("""
            {
              "title": "Parcel: {name} <script>alert(1)</script>",
              "description": "Population is {population}",
              "fieldInfos": [
                { "fieldName": "name", "label": "Display Name", "visible": true },
                { "fieldName": "population", "label": "Pop", "visible": true },
                { "fieldName": "hidden_by_popup", "label": "Hidden", "visible": false },
                { "fieldName": "secret_code", "label": "Secret", "visible": true }
              ]
            }
            """);

        var resource = CreateResource(popupInfo: popupDoc.RootElement.Clone());
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "Honolulu & <Maui>",
            ["population"] = 350000,
            ["hidden_by_popup"] = "do-not-show"
            // secret_code already excluded by BuildVisibleFeatureInfoAttributes
        };

        var html = WmsPopupHtmlFormatter.BuildHtml([
            new WmsPopupHtmlFormatter.FeatureEntry("parcels", resource, attributes)
        ]);

        html.Should().Contain("<h2>Parcel: Honolulu &amp; &lt;Maui&gt; &lt;script&gt;alert(1)&lt;/script&gt;</h2>");
        html.Should().Contain("<p class=\"honua-popup-description\">Population is 350000</p>");
        html.Should().Contain("<tr><th>Display Name</th><td>Honolulu &amp; &lt;Maui&gt;</td></tr>");
        html.Should().Contain("<tr><th>Pop</th><td>350000</td></tr>");
        html.Should().NotContain("do-not-show");
        html.Should().NotContain("Secret");
        html.Should().NotContain("<script>");
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task HandleWms_GetMap_SupportsPng8WebpDpiAndCqlFilter_AndLeavesDefaultPngAndJpegUnchanged()
    {
        var pointWkb = new WKBWriter().Write(new Point(0, 0));
        var feature = new Feature
        {
            Id = 1,
            Geometry = pointWkb,
            Attributes = ImmutableDictionary<string, object?>.Empty
                .Add("name", "Honolulu")
                .Add("population", 350000)
        };
        FeatureQuery? capturedQuery = null;
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedQuery = call.Arg<FeatureQuery>();
                return Task.FromResult(new QueryResult<Feature>
                {
                    Items = [feature],
                    TotalCount = 1
                });
            });

        var resource = CreateResource();
        await using var serviceProvider = BuildWmsServices(resource, reader);

        // 1. GetCapabilities advertises image/png8, image/webp, and text/html
        var (capStatus, capType, capBytes) = await ExecuteWmsAsync(
            serviceProvider,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetCapabilities");
        capStatus.Should().Be(StatusCodes.Status200OK);
        var capXml = Encoding.UTF8.GetString(capBytes);
        capXml.Should().Contain("<Format>image/png8</Format>");
        capXml.Should().Contain("<Format>image/webp</Format>");
        capXml.Should().Contain("<Format>text/html</Format>");

        // 2. GetMap with FORMAT=image/png8 and CQL_FILTER
        var (png8Status, png8Type, png8Bytes) = await ExecuteWmsAsync(
            serviceProvider,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png8&CQL_FILTER=population%20%3E%20100");
        png8Status.Should().Be(StatusCodes.Status200OK);
        png8Type.Should().Be("image/png");
        png8Bytes[25].Should().Be(3); // 8-bit indexed PNG
        using (var png8Bmp = SKBitmap.Decode(png8Bytes))
        {
            png8Bmp.Should().NotBeNull();
            png8Bmp!.Width.Should().Be(64);
        }
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.SqlFilter.Should().NotBeNull();

        // 3. GetMap with FORMAT=image/webp
        var (webpStatus, webpType, webpBytes) = await ExecuteWmsAsync(
            serviceProvider,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/webp");
        webpStatus.Should().Be(StatusCodes.Status200OK);
        webpType.Should().Be("image/webp");
        Encoding.ASCII.GetString(webpBytes, 8, 4).Should().Be("WEBP");
        using (var webpBmp = SKBitmap.Decode(webpBytes))
        {
            webpBmp.Should().NotBeNull();
            webpBmp!.Width.Should().Be(64);
        }

        // 4. GetMap with FORMAT_OPTIONS=dpi:270 scales drawn point larger than default PNG
        var (defaultPngStatus, _, defaultPngBytes) = await ExecuteWmsAsync(
            serviceProvider,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png");
        var (dpiPngStatus, _, dpiPngBytes) = await ExecuteWmsAsync(
            serviceProvider,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png&FORMAT_OPTIONS=dpi:270");
        defaultPngStatus.Should().Be(StatusCodes.Status200OK);
        dpiPngStatus.Should().Be(StatusCodes.Status200OK);
        CountNonWhitePixels(dpiPngBytes).Should().BeGreaterThan(CountNonWhitePixels(defaultPngBytes));

        // 5. Default PNG and JPEG without new parameters are deterministic and valid
        var (defaultPngStatus2, defaultPngType2, defaultPngBytes2) = await ExecuteWmsAsync(
            serviceProvider,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png");
        defaultPngStatus2.Should().Be(StatusCodes.Status200OK);
        defaultPngType2.Should().Be("image/png");
        defaultPngBytes2.Should().Equal(defaultPngBytes);

        var (jpegStatus, jpegType, jpegBytes) = await ExecuteWmsAsync(
            serviceProvider,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/jpeg");
        jpegStatus.Should().Be(StatusCodes.Status200OK);
        jpegType.Should().Be("image/jpeg");
        jpegBytes[0..2].Should().Equal(0xFF, 0xD8);

        // 6. GetFeatureInfo with INFO_FORMAT=text/html renders popup title and fields
        using var popupDoc = JsonDocument.Parse("""{"title":"City: {name}","fieldInfos":[{"fieldName":"population","label":"Population","visible":true}]}""");
        var popupResource = CreateResource(popupInfo: popupDoc.RootElement.Clone());
        await using var popupSp = BuildWmsServices(popupResource, reader);
        var (htmlStatus, htmlType, htmlBytes) = await ExecuteWmsAsync(
            popupSp,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&LAYERS=parcels&QUERY_LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&I=32&J=32&INFO_FORMAT=text/html");
        htmlStatus.Should().Be(StatusCodes.Status200OK);
        htmlType.Should().StartWith("text/html");
        var htmlText = Encoding.UTF8.GetString(htmlBytes);
        htmlText.Should().Contain("<h2>City: Honolulu</h2>");
        htmlText.Should().Contain("<tr><th>Population</th><td>350000</td></tr>");

        // 7. GetFeatureInfo accepts valid CQL_FILTER and rejects invalid CQL_FILTER with an OGC exception
        var (gfiCqlStatus, _, _) = await ExecuteWmsAsync(
            popupSp,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&LAYERS=parcels&QUERY_LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&I=32&J=32&INFO_FORMAT=text/html&CQL_FILTER=population+%3E+100000");
        gfiCqlStatus.Should().Be(StatusCodes.Status200OK);

        var (gfiBadCqlStatus, _, gfiBadCqlBytes) = await ExecuteWmsAsync(
            popupSp,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&LAYERS=parcels&QUERY_LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&I=32&J=32&INFO_FORMAT=text/html&CQL_FILTER=population+%3E");
        gfiBadCqlStatus.Should().Be(StatusCodes.Status400BadRequest);
        Encoding.UTF8.GetString(gfiBadCqlBytes).Should().Contain("InvalidParameterValue");

        // 8. GetMap rejects invalid FORMAT_OPTIONS=dpi: with InvalidParameterValue
        var (badDpiStatus, _, badDpiBytes) = await ExecuteWmsAsync(
            serviceProvider,
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&FORMAT=image/png&FORMAT_OPTIONS=dpi:notanumber");
        badDpiStatus.Should().Be(StatusCodes.Status400BadRequest);
        Encoding.UTF8.GetString(badDpiBytes).Should().Contain("InvalidParameterValue");
    }

    [Fact]
    public void TryNormalizeFeatureInfoFormat_GatesHtmlToWmsOnly()
    {
        OgcClassicRequestHelpers.TryNormalizeFeatureInfoFormat("text/html", out _).Should().BeFalse();
        OgcClassicRequestHelpers.TryNormalizeFeatureInfoFormat("text/html", out var normalized, allowHtml: true).Should().BeTrue();
        normalized.Should().Be("text/html");
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

    private ServiceProvider BuildWmsServices(
        MetadataV2Object resource,
        IFeatureReader reader)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-1",
                Name = "test-service",
                Title = "Test Service"
            },
            Protocols = ["Wms"],
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new AccessPolicy { AllowAnonymous = true },
            SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true }
        };

        var layer = new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "pub-1",
                Name = "parcels"
            },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            LayerIndex = 1,
            ServiceLocalId = "parcels",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
        };

        var graph = new MetadataV2Graph
        {
            Services = [service],
            Resources = [resource],
            Publications = [layer]
        };
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UtcNow);
        IMetadataV2GraphProvider graphProvider = new StaticGraphProvider(snapshot);

        var resourceValidator = Substitute.For<IResourceValidator>();
        resourceValidator.ValidateServiceV2Async("test-service", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResourceValidationResult.Success(service, snapshot)));

        var styleCatalog = Substitute.For<ILayerStyleCatalog>();
        var routeValidator = Substitute.For<IRouteParameterValidator>();
        routeValidator.ValidateServiceId(Arg.Any<HttpContext>())
            .Returns(ValidationResult<string>.Success("test-service"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddValidationServices();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(routeValidator);
        services.AddSingleton(resourceValidator);
        services.AddSingleton(graphProvider);
        services.AddSingleton(reader);
        services.AddSingleton(styleCatalog);
        services.AddSingleton<IFilterExpressionService>(new FilterExpressionService(new FilterExpressionTranslator(_sql)));
        services.AddSingleton<RasterRenderCapacityLimiter>();
        return services.BuildServiceProvider();
    }

    private static async Task<(int Status, string ContentType, byte[] Body)> ExecuteWmsAsync(
        IServiceProvider serviceProvider,
        string queryString)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/rest/services/test-service/MapServer/WMS";
        context.Request.RouteValues["serviceId"] = "test-service";
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();

        var result = await WmsRequestHandlers.HandleWms(context);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var ms = new MemoryStream();
        await context.Response.Body.CopyToAsync(ms);
        return (context.Response.StatusCode, context.Response.ContentType ?? string.Empty, ms.ToArray());
    }

    private (SqlFragment? Filter, IResult? Error) TranslateCql(string cqlFilter)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        context.Request.QueryString = new QueryString("?VERSION=1.3.0");
        return WmsRequestHandlers.TranslateWmsLayerCqlFilter(
            context,
            new FilterExpressionService(new FilterExpressionTranslator(_sql)),
            CreateResource(),
            cqlFilter);
    }

    private async Task<(int Status, string Body)> TranslateCqlAsync(string cqlFilter)
    {
        var (_, error) = TranslateCql(cqlFilter);
        error.Should().NotBeNull();
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Response.Body = new MemoryStream();
        await error!.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static MetadataV2Object CreateResource(JsonElement? popupInfo = null)
    {
        var extensions = popupInfo.HasValue
            ? new Dictionary<string, JsonElement> { ["geoservices:popupInfo"] = popupInfo.Value }
            : new Dictionary<string, JsonElement>();

        return new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "res-1",
                Name = "parcels",
                Title = "Parcels"
            },
            Type = MetadataV2ObjectType.FeatureDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new AccessPolicy { AllowAnonymous = true },
            Spatial = new MetadataV2ObjectSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true }
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "name", Alias = "Parcel Name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "population", Alias = "Population", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "secret_code", Type = MetadataV2FieldType.String, Hidden = true },
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry }
            ],
            Extensions = extensions
        };
    }

    private sealed class StaticGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(snapshot);
    }
}
