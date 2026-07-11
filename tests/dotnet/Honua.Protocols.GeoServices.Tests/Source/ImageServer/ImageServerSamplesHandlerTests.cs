// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
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
    public async Task GetSamplesAsync_WithMultiplePoints_ResolvesRegistrationOnce()
    {
        var (store, _) = await BuildVerticalZarrStoreAsync();
        var handler = CreateHandler(store);
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["geometry"] = "{\"points\":[[0,0],[90,45]],\"spatialReference\":{\"wkid\":4326}}",
            ["multidimensionalDefinition"] =
                "[{\"variableName\":\"temperature\",\"dimensionName\":\"elevation\",\"values\":[333.3333]}]",
        };
        var context = CreateImageServerContext();

        var result = await handler.GetSamplesAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("samples").GetArrayLength().Should().Be(2);
        await store.Received(1).ListByLayerAsync(1, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ReadAsync_WithDuplicateDimensions_ReturnsInvalidSelection()
    {
        Activity? stoppedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Honua.Core.Raster.Zarr",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity,
        };
        ActivitySource.AddActivityListener(listener);
        var store = Substitute.For<IZarrStore>();
        var reader = new ZarrPointSliceReader(
            store,
            new ZarrSubsetReader(),
            Array.Empty<ICloudRangeReader>());

        var result = await reader.ReadAsync(
            1,
            0,
            0,
            4326,
            [
                new ZarrPointSliceSelection("temperature", "elevation", 100),
                new ZarrPointSliceSelection("temperature", "ELEVATION", 200),
            ]);

        result.Status.Should().Be(ZarrPointSliceReadStatus.InvalidSelection);
        result.Error.Should().Contain("may be selected only once");
        await store.DidNotReceiveWithAnyArgs().ListByLayerAsync(default, default);
        stoppedActivity.Should().NotBeNull();
        stoppedActivity!.Status.Should().Be(ActivityStatusCode.Unset);
        stoppedActivity.TagObjects.Should().Contain(
            tag => tag.Key == "honua.slice.failure_count" && Equals(tag.Value, 1));
        stoppedActivity.TagObjects.Should().Contain(
            tag => tag.Key == "honua.slice.read_failure_count" && Equals(tag.Value, 0));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ReadAsync_FirstUnavailableSecondReadable_UsesReadableRegistration()
    {
        var (_, metadata) = await BuildVerticalZarrStoreAsync();
        var unavailable = CreateRegistration(1, CloudStorageProvider.AzureBlob, metadata);
        var readable = CreateRegistration(2, CloudStorageProvider.AwsS3, metadata);
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns([unavailable, readable]);
        var reader = new ZarrPointSliceReader(
            store,
            new ZarrSubsetReader(),
            [_currentRangeReader]);

        var result = await reader.ReadAsync(
            1,
            0,
            0,
            4326,
            [new ZarrPointSliceSelection("temperature", "elevation", 333.3333)]);

        result.Status.Should().Be(ZarrPointSliceReadStatus.Success);
        result.Value.Should().Be(1022);
        await store.Received(1).ListByLayerAsync(1, Arg.Any<CancellationToken>());
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
        var sampler = new ZarrPointSliceReader(
            zarrStore,
            new ZarrSubsetReader(),
            new[] { (ICloudRangeReader)_currentRangeReader });
        return new ImageServerSamplesHandler(
            _graphProvider,
            _rasterStore,
            sampler,
            NullLogger<ImageServerSamplesHandler>.Instance);
    }

    private ImageServerFixtureRangeReader _currentRangeReader = new(new Dictionary<string, byte[]>());

    private async Task<(IZarrStore Store, ZarrStoreMetadata Metadata)> BuildVerticalZarrStoreAsync()
    {
        var objects = ImageServerZarrTestFixture.BuildVerticalStore("stores/vertical", levels: 4, rows: 4, cols: 4);
        _currentRangeReader = new ImageServerFixtureRangeReader(objects);

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(_currentRangeReader, "bucket", "stores/vertical");

        var registration = CreateRegistration(1, CloudStorageProvider.AwsS3, metadata);

        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns([registration]);
        return (store, metadata);
    }

    private static ZarrRegistration CreateRegistration(
        long id,
        CloudStorageProvider provider,
        ZarrStoreMetadata metadata)
        => new()
        {
            Id = id,
            LayerId = 1,
            Name = $"vertical-{id}",
            Provider = provider,
            Bucket = "bucket",
            RootPath = "stores/vertical",
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };

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

}
