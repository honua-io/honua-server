// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>Scientific slice values through the real scanner, subset reader and export handler.</summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerMultidimensionalRawSamplesNewTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImage_TwoTimeSlices_PreservesFloatSamplesMasksAndDocumentedMosaicSelection()
    {
        const string root = "synthetic/md-raw";
        var first = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var times = new[] { first.ToUnixTimeMilliseconds(), first.AddDays(1).ToUnixTimeMilliseconds() };
        // Nine pixels cross both a row and a mask-byte boundary. The sentinel is
        // deliberately distinct from valid zero, fractional and negative values.
        double[][] expected = [[.25, -1.5, -9999, 3.125, 0, 5.5, 6.75, 7.25, -9999],
                               [-9999, 11.5, 12.75, 13.125, 14, 15.5, 16.75, -9999, 18.25]];
        foreach (var size in new[] { sizeof(float), sizeof(double) })
        {
            var frames = expected.Select(static values => (double[])values.Clone()).ToArray();
            if (size == sizeof(double))
            {
                frames[1][4] = 16777217.125; // Cannot survive float32 quantization.
            }
            var payload = Samples(frames.SelectMany(static values => values).ToArray(), size);
            var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [root + "/.zgroup"] = Encoding.UTF8.GetBytes("{\"zarr_format\":2}"),
                [root + "/.zattrs"] = Encoding.UTF8.GetBytes("""
                    {"variables":["temperature"],"primary_variable":"temperature","crs_wkid":4326,
                     "extent":[0,0,3,3],"x_dimension":"x","y_dimension":"y","t_dimension":"StdTime",
                     "t_start":"2026-01-01T00:00:00Z","t_end":"2026-01-02T00:00:00Z","t_step_seconds":86400}
                    """),
                [root + "/temperature/.zarray"] = Encoding.UTF8.GetBytes(
                    "{\"chunks\":[1,3,3],\"compressor\":null,\"dtype\":\"<f" + size +
                    "\",\"fill_value\":-9999,\"filters\":null,\"order\":\"C\",\"shape\":[2,3,3],\"zarr_format\":2}"),
                [root + "/temperature/.zattrs"] = Encoding.UTF8.GetBytes("{\"_ARRAY_DIMENSIONS\":[\"StdTime\",\"y\",\"x\"]}"),
                [root + "/temperature/0.0.0"] = payload[..(9 * size)],
                [root + "/temperature/1.0.0"] = payload[(9 * size)..],
            };
            var rangeReader = new ImageServerFixtureRangeReader(objects);
            var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(rangeReader, "bucket", root);
            metadata.Temporal!.StepCount.Should().Be(2);
            var registration = new ZarrRegistration
            {
                Id = 81, LayerId = 1, Name = "synthetic-md", Provider = CloudStorageProvider.AwsS3,
                Bucket = "bucket", RootPath = root, Metadata = metadata, CreatedAt = DateTimeOffset.UnixEpoch,
            };
            var zarr = Substitute.For<IZarrStore>();
            zarr.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns([registration]);
            var sliceReader = new ZarrRasterSliceReader(zarr, new ZarrSubsetReader(), [rangeReader]);
            var graph = new TestMetadataV2GraphBuilder()
                .AddResource("resource-1", "synthetic-md", MetadataV2ResourceType.RasterDataset)
                .AddService("service-1", "image-svc-1", protocols: [ServiceProtocols.ImageServer])
                .AddPublication("publication-1", "service-1", "resource-1", layerIndex: 1,
                    serviceLocalId: "synthetic-md", publicationType: MetadataV2PublicationType.EsriImageLayer)
                .BuildProvider();
            var rasterStore = Substitute.For<IRasterStore>();
            var temporary = Substitute.For<ITemporaryFileService>();
            var handler = new ImageServerExportHandler(graph, new ImageServerExportBackend(rasterStore, sliceReader),
                temporary, NullLogger<ImageServerExportHandler>.Instance);
            for (var frame = 0; frame < 2; frame++)
            {
                var definition = "[{\"variableName\":\"temperature\",\"dimensionName\":\"StdTime\",\"values\":[" + times[frame] + "]}]";
                var mosaic = "{\"mosaicMethod\":\"esriMosaicAttribute\",\"sortField\":\"AcquisitionDate\",\"sortValue\":\"2026/01/01\",\"multidimensionalDefinition\":" + definition + "}";
                foreach (var format in new[] { "bip", "bsq" })
                {
                    var request = Request(format, size == sizeof(float) ? "F32" : "F64",
                        format == "bip" ? definition : null, format == "bsq" ? mosaic : null);
                    var output = await ExportAsync(handler, request);
                    output.Status.Should().Be(200);
                    output.Type.Should().Be("application/octet-stream");
                    byte[] mask = frame == 0 ? [0xdf, 0x00] : [0x7e, 0x80];
                    output.Body.Should().Equal([.. Samples(frames[frame], size), .. mask]);
                    if (frame == 0)
                    {
                        output.Body[..4].Should().Equal(size == 4 ? [0, 0, 0x80, 0x3e] : [0, 0, 0, 0]);
                    }
                }
                // Naming the source float type also remains valid on the existing
                // display path; only the container pixels are stretched to RGBA.
                var png = await ExportAsync(handler, Request("png", size == 4 ? "F32" : "F64", definition, null));
                png.Status.Should().Be(200);
                png.Body[..8].Should().Equal([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
                // Explicit type conversion and ambiguous/filtering rules are rejected.
                (await ExportAsync(handler, Request("bip", size == 4 ? "F64" : "F32", definition, null)))
                    .Status.Should().Be(501);
                (await ExportAsync(handler, Request("bsq", null, definition, mosaic)))
                    .Status.Should().Be(400);
                (await ExportAsync(handler, Request("bsq", null, null,
                    mosaic[..^1] + ",\"where\":\"1=0\"}"))).Status.Should().Be(400);
            }
            rasterStore.ReceivedCalls().Should().BeEmpty("MD selection must read actual Zarr chunks, not a catalog raster fallback");
            temporary.ReceivedCalls().Should().BeEmpty("inline output must not create public temporary artifacts");
            objects[root + "/temperature/0.0.0"].Should().Equal(payload[..(9 * size)]);
        }
    }

    private static ExportImageRequest Request(string format, string? pixelType, string? definition, string? mosaic)
        => new() { Bbox = "0,0,3,3", BboxSr = "4326", ImageSr = "4326", Size = "3,3", Format = format,
            PixelType = pixelType, MultidimensionalDefinition = definition, MosaicRule = mosaic, F = "image" };

    private static byte[] Samples(double[] values, int size)
    {
        var bytes = new byte[values.Length * size];
        for (var index = 0; index < values.Length; index++)
        {
            if (size == 4) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * size), (float)values[index]);
            else BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(index * size), values[index]);
        }
        return bytes;
    }

    private static async Task<(int Status, string? Type, byte[] Body)> ExportAsync(
        ImageServerExportHandler handler, ExportImageRequest request)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([], "synthetic-unit")) };
        context.Request.Scheme = "https"; context.Request.Host = new HostString("unit.example.test");
        context.Response.Body = new MemoryStream();
        await (await handler.ExportImageAsync(context, 1, request)).ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.ContentType, ((MemoryStream)context.Response.Body).ToArray());
    }
}
