// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
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
/// <c>multidimensionalDefinition</c> request handling added in #1869.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerSamplesHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerSamplesHandler _handler;

    public ImageServerSamplesHandlerTests()
    {
        _handler = new ImageServerSamplesHandler(
            _graphProvider,
            _rasterStore,
            NullLogger<ImageServerSamplesHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_WithMultidimensionalDefinition_ReturnsNotImplemented()
    {
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["geometry"] = "{\"x\":0,\"y\":0,\"spatialReference\":{\"wkid\":4326}}",
            ["multidimensionalDefinition"] = "[{\"dimensionName\":\"StdZ\",\"values\":[10]}]",
        };

        var context = CreateImageServerContext();
        var result = await _handler.GetSamplesAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        // Per-slice sampling of a multidimensional cube is parsed/validated but deferred (#1869).
        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyAsync(default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_WithMalformedMultidimensionalDefinition_ReturnsBadRequest()
    {
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["geometry"] = "{\"x\":0,\"y\":0,\"spatialReference\":{\"wkid\":4326}}",
            ["multidimensionalDefinition"] = "not-json",
        };

        var context = CreateImageServerContext();
        var result = await _handler.GetSamplesAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
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
}
