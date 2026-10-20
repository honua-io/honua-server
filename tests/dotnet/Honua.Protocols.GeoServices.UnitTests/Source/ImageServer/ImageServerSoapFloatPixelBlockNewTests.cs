// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>Only new typed GetImage coverage; no host, socket, database or licensed client.</summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerSoapFloatPixelBlockNewTests
{
    private const string Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string ArcGis = "http://www.esri.com/schemas/ArcGIS/10.8";

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetImage_FloatTwoBands_UsesCanonicalTypedBipOrBsqAndRealMask()
    {
        // Source is planar float32, with independent per-band masks and a ninth
        // all-NoData pixel. The test crosses a mask byte/row boundary and retains
        // fractional/negative values that PNG conversion cannot reproduce.
        float[] planar = [.5f, -9999f, 2.75f, -3.25f, 4.5f, 5.125f, 6.25f, 7.5f, -9999f,
                          10.25f, 11.5f, 12.75f, 13f, -9998f, 15.5f, 16.75f, 17f, -9998f];
        float[] bip = [.5f, 10.25f, -9999f, 11.5f, 2.75f, 12.75f,
                       -3.25f, 13f, 4.5f, -9998f, 5.125f, 15.5f,
                       6.25f, 16.75f, 7.5f, 17f, -9999f, -9998f];
        var source = new RasterResult
        {
            Data = LittleEndian(planar), ContentType = "application/octet-stream", Width = 3, Height = 3,
            BandCount = 2, PixelType = "32BF", Srid = 4326,
            BandValidityMasks = [[0xbf, 0x00], [0xf7, 0x00]]
        };
        var raster = new RasterInfo
        {
            Id = 71, LayerId = 1, Name = "synthetic-float-two-band", Width = 3, Height = 3,
            BandCount = 2, PixelType = "32BF", Srid = 4326, GeoTransform = [0, 1, 0, 3, 0, -1],
            Extent = new RasterExtent { XMin = 0, YMin = 0, XMax = 3, YMax = 3, Srid = 4326 },
            CreatedAt = DateTime.UnixEpoch
        };
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource-1", "synthetic-raster", MetadataV2ResourceType.RasterDataset)
            .AddService("service-1", "image-svc-1", protocols: [ServiceProtocols.ImageServer])
            .AddPublication("publication-1", "service-1", "resource-1", layerIndex: 1,
                serviceLocalId: "synthetic-raster", publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();
        var store = Substitute.For<IRasterStore>();
        store.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>()).Returns(raster);
        store.QueryRastersAsync(1, Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>()).Returns([raster]);
        var queries = new List<RasterQuery>();
        store.ExportImageAsync(1, 71, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => { queries.Add(call.ArgAt<RasterQuery>(2)); return source; });
        var temporary = Substitute.For<ITemporaryFileService>();
        var handler = new ImageServerExportHandler(graph,
            new ImageServerExportBackend(store, Substitute.For<IZarrRasterSliceReader>()), temporary,
            NullLogger<ImageServerExportHandler>.Instance);
        var resolver = Substitute.For<IImageServerLayerResolver>();
        var resolution = new ImageServerLayerResolution(1, "publication-1", 1, null) { ServiceId = "image-svc-1" };
        resolver.ResolveFirstAccessibleLayerAsync("image-svc-1", Arg.Any<HttpContext>(),
                AuthorizationOperation.Export, Arg.Any<CancellationToken>(), true).Returns(resolution);
        resolver.ValidatePublicationAsync("image-svc-1", "publication-1", 1, 1, Arg.Any<HttpContext>(),
                AuthorizationOperation.Export, Arg.Any<CancellationToken>()).Returns(resolution);

        // Invoke the actual SOAP endpoint on in-memory request/response streams.
        // No live HTTP request, TestServer, WebApplicationFactory or alternate
        // encoder is used to replace the production path being checked.
        var defaultBip = await InvokeAsync("", resolver, store, handler);
        defaultBip.Status.Should().Be(StatusCodes.Status200OK);
        var result = defaultBip.Xml.Descendants().Single(e => e.Name.LocalName == "Result");
        result.Parent!.Name.Should().Be(XName.Get("GetImageResponse", ArcGis));
        var bytes = Convert.FromBase64String(result.Value);
        bytes.Should().Equal([.. LittleEndian(bip), 0xff, 0x00]);
        bytes[..8].Should().Equal([0x00, 0x00, 0x00, 0x3f, 0x00, 0x00, 0x24, 0x41]);
        queries.Should().ContainSingle().Which.OutputFormat.Should().Be(RasterFormat.Raw);
        source.Data.Should().Equal(LittleEndian(planar));
        source.BandValidityMasks![0].Should().Equal([0xbf, 0x00]);

        var bsq = await InvokeAsync("<BSQ>true</BSQ>", resolver, store, handler);
        bsq.Status.Should().Be(StatusCodes.Status200OK);
        Convert.FromBase64String(bsq.Xml.Descendants().Single(e => e.Name.LocalName == "Result").Value)
            .Should().Equal([.. LittleEndian(planar), 0xff, 0x00]);
        queries.Should().HaveCount(2).And.OnlyContain(q => q.OutputFormat == RasterFormat.Raw);

        // Invalid/ambiguous layout and unsupported compression fail before any
        // canonical store export, rather than silently changing the wire format.
        foreach (var invalid in new[] { "<BSQ>maybe</BSQ>", "<BSQ>true</BSQ><BSQ>false</BSQ>" })
        {
            var rejected = await InvokeAsync(invalid, resolver, store, handler);
            rejected.Status.Should().Be(StatusCodes.Status400BadRequest);
            rejected.Xml.Descendants().Should().Contain(e => e.Name.LocalName == "Fault");
        }
        var compressed = await InvokeAsync("", resolver, store, handler, "LZ77");
        compressed.Status.Should().Be(StatusCodes.Status400BadRequest);
        queries.Should().HaveCount(2);
        temporary.ReceivedCalls().Should().BeEmpty("inline typed GetImage never creates temporary files");
    }

    private static byte[] LittleEndian(float[] values)
    {
        var result = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(i * sizeof(float)), values[i]);
        }
        return result;
    }

    private static async Task<(int Status, XDocument Xml)> InvokeAsync(string bsq,
        IImageServerLayerResolver resolver, IRasterStore store, ImageServerExportHandler handler, string compression = "None")
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([], "synthetic-unit")) };
        context.Request.Scheme = "https"; context.Request.Host = new HostString("unit.example.test");
        context.Request.Path = "/services/image-svc-1/ImageServer";
        context.Request.ContentType = "text/xml";
        var body = Encoding.UTF8.GetBytes($"""
            <soap:Envelope xmlns:soap="{Soap}"><soap:Body><GetImage xmlns="{ArcGis}">
              <ImageDescription><Extent><XMin>0</XMin><YMin>0</YMin><XMax>3</XMax><YMax>3</YMax>
                <SpatialReference><WKID>4326</WKID></SpatialReference></Extent>
                <Width>3</Width><Height>3</Height><PixelType>F32</PixelType><Compression>{compression}</Compression>{bsq}
              </ImageDescription></GetImage></soap:Body></soap:Envelope>
            """);
        context.Request.Body = new MemoryStream(body); context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        var endpoint = typeof(ImageServerSoapEndpoints).GetMethod("HandlePostSoapImageServer", BindingFlags.NonPublic | BindingFlags.Static)!;
        var pending = (Task<IResult>)endpoint.Invoke(null, ["image-svc-1", context, resolver, store, handler,
            NullLogger<ImageServerSoapLog>.Instance])!;
        var result = await pending;
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, XDocument.Load(context.Response.Body));
    }
}
