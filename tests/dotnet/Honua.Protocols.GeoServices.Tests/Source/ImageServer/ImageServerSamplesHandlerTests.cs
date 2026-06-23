// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for <see cref="ImageServerSamplesHandler"/>, focused on the
/// <c>multidimensionalDefinition</c> per-slice sampling added in #1869.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerSamplesHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_WithMultidimensionalDefinition_NoZarrStore_ReturnsNotImplemented()
    {
        var handler = CreateHandler(Substitute.For<IZarrStore>());
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["geometry"] = "{\"x\":0,\"y\":0,\"spatialReference\":{\"wkid\":4326}}",
            ["multidimensionalDefinition"] = "[{\"dimensionName\":\"elevation\",\"values\":[10]}]",
        };

        var context = CreateImageServerContext();
        var result = await handler.GetSamplesAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        // No servable Zarr store for the layer: honest 501 rather than sampling the collapsed raster.
        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyAsync(default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_WithMalformedMultidimensionalDefinition_ReturnsBadRequest()
    {
        var handler = CreateHandler(Substitute.For<IZarrStore>());
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["geometry"] = "{\"x\":0,\"y\":0,\"spatialReference\":{\"wkid\":4326}}",
            ["multidimensionalDefinition"] = "not-json",
        };

        var context = CreateImageServerContext();
        var result = await handler.GetSamplesAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_WithVerticalSlice_SamplesZarrStore()
    {
        var (store, _) = await BuildVerticalZarrStoreAsync();
        var handler = CreateHandler(store);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            // Point at the centre of a 4326 world extent (col 2, row 2 of a 4x4 grid).
            ["geometry"] = "{\"x\":0,\"y\":0,\"spatialReference\":{\"wkid\":4326}}",
            // elevation 500 -> level index 2 on a {0..1000, 5 samples} axis is index 2 (here 4 levels).
            ["multidimensionalDefinition"] = "[{\"variableName\":\"temperature\",\"dimensionName\":\"elevation\",\"values\":[333.3333],\"isSlice\":true}]",
        };

        var context = CreateImageServerContext();
        var result = await handler.GetSamplesAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        // Sample value is encoded as level*1000 + row*10 + col; level 1, row 2, col 2 -> 1022.
        body.Should().Contain("1022");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_WithUnknownDimension_ReturnsBadRequest()
    {
        var (store, _) = await BuildVerticalZarrStoreAsync();
        var handler = CreateHandler(store);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["geometry"] = "{\"x\":0,\"y\":0,\"spatialReference\":{\"wkid\":4326}}",
            ["multidimensionalDefinition"] = "[{\"dimensionName\":\"salinity\",\"values\":[10]}]",
        };

        var context = CreateImageServerContext();
        var result = await handler.GetSamplesAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    private ImageServerSamplesHandler CreateHandler(IZarrStore zarrStore)
    {
        var sampler = new ZarrPointSampler(
            zarrStore,
            new ZarrSubsetReader(),
            new[] { (ICloudRangeReader)_currentRangeReader });
        return new ImageServerSamplesHandler(
            _graphProvider,
            _rasterStore,
            sampler,
            NullLogger<ImageServerSamplesHandler>.Instance);
    }

    private FixtureRangeReader _currentRangeReader = new(new Dictionary<string, byte[]>());

    private async Task<(IZarrStore Store, ZarrStoreMetadata Metadata)> BuildVerticalZarrStoreAsync()
    {
        var objects = ImageServerZarrFixture.BuildVerticalStore("stores/vertical", levels: 4, rows: 4, cols: 4);
        _currentRangeReader = new FixtureRangeReader(objects);

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(_currentRangeReader, "bucket", "stores/vertical");

        var registration = new ZarrRegistration
        {
            Id = 1,
            LayerId = 1,
            Name = "vertical",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = "stores/vertical",
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns([registration]);
        return (store, metadata);
    }

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/getSamples";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static TestMetadataV2GraphProvider BuildGraphWithLayer(int layerIndex)
        => new TestMetadataV2GraphBuilder()
            .AddResource($"resource-{layerIndex}", "test-layer", MetadataV2ResourceType.RasterDataset)
            .AddService($"service-{layerIndex}", $"image-svc-{layerIndex}", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                $"publication-{layerIndex}",
                $"service-{layerIndex}",
                $"resource-{layerIndex}",
                layerIndex: layerIndex,
                serviceLocalId: "test-layer",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();

    /// <summary>Object-store double serving a fixture path-to-bytes map.</summary>
    private sealed class FixtureRangeReader : ICloudRangeReader
    {
        private readonly Dictionary<string, byte[]> _objects;

        public FixtureRangeReader(Dictionary<string, byte[]> objects) => _objects = objects;

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(key, out var data))
            {
                throw new FileNotFoundException(key);
            }
            var start = (int)offset;
            var available = data.Length - start;
            if (available <= 0)
            {
                return Task.FromResult(Array.Empty<byte>());
            }
            var count = Math.Min(length, available);
            var slice = new byte[count];
            Buffer.BlockCopy(data, start, slice, 0, count);
            return Task.FromResult(slice);
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(key, out var data))
            {
                throw new FileNotFoundException(key);
            }
            return Task.FromResult<Stream>(new MemoryStream(data, (int)offset, Math.Min(length, data.Length - (int)offset)));
        }

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(key, out var data))
            {
                throw new FileNotFoundException(key);
            }
            return Task.FromResult((long)data.Length);
        }
    }

    /// <summary>Builds a 3D (elevation, y, x) Zarr v2 store with a vertical axis manifest.</summary>
    private static class ImageServerZarrFixture
    {
        public static Dictionary<string, byte[]> BuildVerticalStore(string root, int levels, int rows, int cols)
        {
            var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [root + "/.zgroup"] = Encoding.UTF8.GetBytes("{\"zarr_format\":2}"),
                [root + "/.zattrs"] = Encoding.UTF8.GetBytes(
                    "{\"variables\":[\"temperature\"],\"primary_variable\":\"temperature\","
                    + "\"crs_wkid\":4326,\"extent\":[-180,-90,180,90],"
                    + "\"x_dimension\":\"x\",\"y_dimension\":\"y\","
                    + "\"axes\":[{\"name\":\"elevation\",\"unit\":\"m\",\"start\":0,\"end\":1000}]}"),
            };

            var arrayRoot = root + "/temperature";
            objects[arrayRoot + "/.zarray"] = Encoding.UTF8.GetBytes(
                "{\"chunks\":[" + levels + "," + rows + "," + cols + "],\"compressor\":null,\"dtype\":\"<f4\","
                + "\"fill_value\":0,\"filters\":null,\"order\":\"C\",\"shape\":[" + levels + "," + rows + "," + cols + "],\"zarr_format\":2}");
            objects[arrayRoot + "/.zattrs"] = Encoding.UTF8.GetBytes("{\"_ARRAY_DIMENSIONS\":[\"elevation\",\"y\",\"x\"]}");

            var raw = new byte[levels * rows * cols * sizeof(float)];
            for (var l = 0; l < levels; l++)
            {
                for (var r = 0; r < rows; r++)
                {
                    for (var c = 0; c < cols; c++)
                    {
                        var offset = ((l * rows + r) * cols + c) * sizeof(float);
                        var value = (float)(l * 1000 + r * 10 + c);
                        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, raw, offset, sizeof(float));
                    }
                }
            }
            objects[arrayRoot + "/0.0.0"] = raw;
            return objects;
        }
    }
}
