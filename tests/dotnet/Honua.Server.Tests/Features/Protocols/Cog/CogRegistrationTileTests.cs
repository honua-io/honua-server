// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Cog;

/// <summary>Exercises the registration-to-ImageServer binding through HTTP and the real COG store.</summary>
/// <remarks>
/// Service A publishes index 1 over storage layer 42; service B publishes index 2 over storage
/// layer 1. The registration's <c>layerId</c> is the service-local index (1), so a lookup keyed
/// by storage id (#4204) would miss A's COG and could serve it to whichever service owns
/// storage layer 1 (B).
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Cog, TestProtocols.ImageServer)]
public sealed class CogRegistrationTileTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.CogAdmin, Operations.GetTile)]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task RegisteredCog_DistinctStorageAndPublicationIds_ServesOnlyBoundService(bool sharedJpegTables)
    {
        var source = await File.ReadAllBytesAsync(Path.Combine(GdalJpegOracle.FixtureDirectory, "deflate_pred1_uint8.tif"));
        if (sharedJpegTables)
        {
            source = AddSharedJpegTables(source);
        }
        var expected = await File.ReadAllBytesAsync(Path.Combine(GdalJpegOracle.FixtureDirectory, "deflate_pred1_uint8.bin"));
        var fixture = await StartAsync(CreateGraph(withCollision: false).BuildProvider(), source);
        try
        {
            await RegisterAsync(fixture);

            using var ownTile = await fixture.Client.GetAsync(
                "/rest/services/imagery-a/ImageServer/tile/8/0/0?format=" + (sharedJpegTables ? "jpg" : "png"));
            if (sharedJpegTables)
            {
                // DEFLATE bytes relabelled as JPEG with empty tables: no frame, so never image/jpeg.
                await ownTile.AssertGeoServicesErrorAsync(404);
                ownTile.Content.Headers.ContentType!.MediaType.Should().NotBe("image/jpeg");
            }
            else
            {
                ownTile.StatusCode.Should().Be(HttpStatusCode.OK);
                ownTile.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
                using var decoded = SkiaSharp.SKBitmap.Decode(await ownTile.Content.ReadAsByteArrayAsync());
                decoded.Should().NotBeNull();
                decoded.Width.Should().Be(128);
                decoded.Height.Should().Be(128);
                for (var row = 0; row < 128; row++)
                {
                    for (var col = 0; col < 128; col++)
                    {
                        var value = expected[row * 128 + col];
                        decoded.GetPixel(col, row).Should().Be(new SkiaSharp.SKColor(value, value, value, 255));
                    }
                }
            }

            using var otherTile = await fixture.Client.GetAsync("/rest/services/imagery-b/ImageServer/tile/8/0/0");
            await otherTile.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData("jpeg_ycbcr_rgb_uint8", 3)]
    [InlineData("jpeg_gray_uint8", 1)]
    [InlineData("jpeg_rgb_uint8", 3)]
    [Operation(Operations.CogAdmin, Operations.GetTile)]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task RegisteredGdalJpegCog_SharedJpegTables_ServesDecodableJpegMatchingGdal(string cog, int bands)
    {
        var source = await File.ReadAllBytesAsync(Path.Combine(GdalJpegOracle.FixtureDirectory, cog + ".tif"));
        var expected = await File.ReadAllBytesAsync(Path.Combine(GdalJpegOracle.FixtureDirectory, cog + ".bin"));
        var fixture = await StartAsync(CreateGraph(withCollision: false).BuildProvider(), source);
        try
        {
            await RegisterAsync(fixture);

            using var tile = await fixture.Client.GetAsync("/rest/services/imagery-a/ImageServer/tile/8/0/0?format=jpg");

            tile.StatusCode.Should().Be(HttpStatusCode.OK);
            tile.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
            var body = await tile.Content.ReadAsByteArrayAsync();
            // #4205 repro step 3: the served body must carry a DQT segment.
            body.AsSpan().IndexOf([(byte)0xFF, (byte)0xDB]).Should().BeGreaterThan(0);
            GdalJpegOracle.AssertMatchesGdalDecode(body, expected, bands);

            using var otherTile = await fixture.Client.GetAsync("/rest/services/imagery-b/ImageServer/tile/8/0/0?format=jpg");
            await otherTile.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.CogAdmin, Operations.GetTile)]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task RegisteredCog_PublicationIndexLaterCollides_FailsClosedForEveryService()
    {
        var source = await File.ReadAllBytesAsync(Path.Combine(GdalJpegOracle.FixtureDirectory, "deflate_pred1_uint8.tif"));
        var graph = CreateGraph(withCollision: false).BuildProvider();
        var fixture = await StartAsync(graph, source);
        try
        {
            await RegisterAsync(fixture);
            using (var bound = await fixture.Client.GetAsync("/rest/services/imagery-a/ImageServer/tile/8/0/0?format=png"))
            {
                bound.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            // Service C now also publishes index 1. The registration row carries no service id,
            // so neither A nor C may be handed the COG (#4204's cross-service disclosure case).
            graph.SetGraph(CreateGraph(withCollision: true).Build(), etag: "\"cog-index-collision\"");

            using var formerOwner = await fixture.Client.GetAsync("/rest/services/imagery-a/ImageServer/tile/8/0/0?format=png");
            await formerOwner.AssertGeoServicesErrorAsync(404);
            formerOwner.Content.Headers.ContentType!.MediaType.Should().NotBe("image/png");
            using var colliding = await fixture.Client.GetAsync("/rest/services/imagery-c/ImageServer/tile/8/0/0?format=png");
            await colliding.AssertGeoServicesErrorAsync(404);
            colliding.Content.Headers.ContentType!.MediaType.Should().NotBe("image/png");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static TestMetadataV2GraphBuilder CreateGraph(bool withCollision)
    {
        var builder = new TestMetadataV2GraphBuilder()
            .AddResource("resource-a", "imagery-a", MetadataV2ResourceType.RasterDataset)
            .AddStorageBinding("binding-a", "resource-a", "rasters:42", storageLayerId: 42)
            .AddService("service-a", "imagery-a", protocols: [ServiceProtocols.ImageServer])
            .AddPublication("publication-a", "service-a", "resource-a", layerIndex: 1,
                storageBindingId: "binding-a", publicationType: MetadataV2PublicationType.EsriImageLayer)
            .AddResource("resource-b", "imagery-b", MetadataV2ResourceType.RasterDataset)
            .AddStorageBinding("binding-b", "resource-b", "rasters:1", storageLayerId: 1)
            .AddService("service-b", "imagery-b", protocols: [ServiceProtocols.ImageServer])
            .AddPublication("publication-b", "service-b", "resource-b", layerIndex: 2,
                storageBindingId: "binding-b", publicationType: MetadataV2PublicationType.EsriImageLayer);
        return withCollision
            ? builder
                .AddResource("resource-c", "imagery-c", MetadataV2ResourceType.RasterDataset)
                .AddStorageBinding("binding-c", "resource-c", "rasters:7", storageLayerId: 7)
                .AddService("service-c", "imagery-c", protocols: [ServiceProtocols.ImageServer])
                .AddPublication("publication-c", "service-c", "resource-c", layerIndex: 1,
                    storageBindingId: "binding-c", publicationType: MetadataV2PublicationType.EsriImageLayer)
            : builder;
    }

    private static async Task<WebAppFixture> StartAsync(TestMetadataV2GraphProvider graph, byte[] source)
    {
        var rangeReader = Substitute.For<ICloudRangeReader>();
        rangeReader.Provider.Returns(CloudStorageProvider.AwsS3);
        rangeReader.GetObjectMetadataAsync("bucket", "bound.tif", Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata { SizeBytes = source.Length, ETag = "etag-1" });
        rangeReader.ReadRangeAsync("bucket", "bound.tif", Arg.Any<long>(), Arg.Any<int>(), "etag-1", Arg.Any<CancellationToken>())
            .Returns(call => source.AsSpan((int)call.ArgAt<long>(2),
                Math.Min(call.ArgAt<int>(3), source.Length - (int)call.ArgAt<long>(2))).ToArray());
        var metadataReader = new Honua.Core.Features.Raster.CogParser.CogMetadataExtractor();
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.QueryRastersAsync(default, default!, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.RemoveAll<IMetadataV2GraphStore>();
            services.AddSingleton<IMetadataV2GraphProvider>(graph);
            services.AddSingleton<IMetadataV2GraphStore>(graph);
            services.AddSingleton(rasterStore);
            // Isolate binding behavior from license provisioning; use the real resolver/store.
            services.AddScoped<ICogTileResolver>(provider => new CogTileResolver([rangeReader], metadataReader,
                provider.GetRequiredService<ICogStore>(), provider.GetRequiredService<IMemoryCache>(),
                NullLogger<CogTileResolver>.Instance));
        });
        await fixture.InitializeAsync();
        return fixture;
    }

    private static async Task RegisterAsync(WebAppFixture fixture)
    {
        using var registration = await fixture.Client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", new
        {
            layerId = 1,
            name = "bound-cog",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "bound.tif"
        });
        registration.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static byte[] AddSharedJpegTables(byte[] source)
    {
        // Relocate the IFD so all existing sample/georeferencing offsets stay intact.
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(ifd));
        var entries = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var entry = source.AsSpan(ifd + 2 + i * 12, 12).ToArray();
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) == 259)
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), 7);
            entries.Add(entry);
        }
        byte[] tables = [0x5B, 0x01, 7, 0, 4, 0, 0, 0, 0xFF, 0xD8, 0xFF, 0xD9];
        entries.Add(tables); // JPEGTables (347), UNDEFINED, inline SOI/EOI.
        var result = new byte[source.Length + 2 + entries.Count * 12 + 4];
        source.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)source.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(source.Length), (ushort)entries.Count);
        var ordered = entries.OrderBy(entry => BinaryPrimitives.ReadUInt16LittleEndian(entry)).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            ordered[i].CopyTo(result, source.Length + 2 + i * 12);
        return result;
    }
}
