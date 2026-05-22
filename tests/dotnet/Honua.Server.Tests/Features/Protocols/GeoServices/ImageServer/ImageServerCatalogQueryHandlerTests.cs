// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Models;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for <see cref="ImageServerCatalogQueryHandler"/>. The handler exposes
/// the raster catalog as Esri-compatible features through the <c>query</c>
/// endpoint, mirroring the FeatureServer envelope.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerCatalogQueryHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerCatalogQueryHandler _handler;

    public ImageServerCatalogQueryHandlerTests()
    {
        var catalogReader = new ImageServerCatalogReader(_rasterStore, new ImageServerCatalogFilterEvaluator());
        _handler = new ImageServerCatalogQueryHandler(
            _graphProvider,
            catalogReader,
            NullLogger<ImageServerCatalogQueryHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_LayerNotFound_ReturnsNotFound()
    {

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 99, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_NoRasters_ReturnsEmptyFeatureSet()
    {
        SetupLayerWithRasters(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, EmptyValues(), CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Features.Should().BeEmpty();
        jsonResult.Value.ExceededTransferLimit.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ReturnsAllCatalogFields()
    {
        SetupLayerWithRasters([CreateRaster(100, "first")]);

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, EmptyValues(), CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Fields.Select(f => f.Name).Should().Contain([
            "OBJECTID",
            "Name",
            "MinPS",
            "MaxPS",
            "LowPS",
            "HighPS",
            "CenterX",
            "CenterY",
            "ZOrder",
            "Shape_Length",
            "Shape_Area",
            "BandCount",
            "PixelType",
            "AcquisitionDate",
            "CreatedAt",
        ]);
        jsonResult.Value.GeometryType.Should().Be("esriGeometryPolygon");
        jsonResult.Value.ObjectIdFieldName.Should().Be("OBJECTID");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_FeatureAttributesContainProjectedValues()
    {
        SetupLayerWithRasters([CreateRaster(100, "scene-a")]);

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, EmptyValues(), CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Features.Should().HaveCount(1);

        var feature = jsonResult.Value.Features[0];
        feature.Attributes["OBJECTID"].Should().Be(100L);
        feature.Attributes["Name"].Should().Be("scene-a");
        feature.Attributes["CenterX"].Should().Be(0.0);
        feature.Attributes["CenterY"].Should().Be(0.0);
        feature.Attributes["BandCount"].Should().Be(1);
        feature.Attributes["PixelType"].Should().Be("8BUI");
        feature.Attributes["CreatedAt"].Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        feature.Geometry.Should().NotBeNull();
        feature.Geometry!.Rings.Should().HaveCount(1);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ReturnGeometryFalse_OmitsFootprint()
    {
        SetupLayerWithRasters([CreateRaster(100, "scene-a")]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["returnGeometry"] = "false",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Features[0].Geometry.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ReturnIdsOnly_ReturnsObjectIdsResponse()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "first"),
            CreateRaster(200, "second"),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["returnIdsOnly"] = "true",
            ["resultOffset"] = "1",
            ["resultRecordCount"] = "1",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogObjectIdsResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.ObjectIds.Should().Equal(100L, 200L);
        jsonResult.Value.ObjectIdFieldName.Should().Be("OBJECTID");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ReturnCountOnly_ReturnsCountResponse()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "first"),
            CreateRaster(200, "second"),
            CreateRaster(300, "third"),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["returnCountOnly"] = "true",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogCountResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Count.Should().Be(3);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ReturnExtentOnly_ReturnsAggregateExtent()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "west", xMin: -10, yMin: -5, xMax: 0, yMax: 5),
            CreateRaster(200, "east", xMin: 0, yMin: -5, xMax: 10, yMax: 5),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["returnExtentOnly"] = "true",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogExtentResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Extent.XMin.Should().Be(-10);
        jsonResult.Value.Extent.YMin.Should().Be(-5);
        jsonResult.Value.Extent.XMax.Should().Be(10);
        jsonResult.Value.Extent.YMax.Should().Be(5);
        jsonResult.Value.Extent.SpatialReference.Wkid.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ObjectIdsFilter_OnlyReturnsMatching()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "first"),
            CreateRaster(200, "second"),
            CreateRaster(300, "third"),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectIds"] = "100,300",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Features.Select(f => f.Attributes["OBJECTID"])
            .Should().BeEquivalentTo([100L, 300L]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ObjectIdsJsonArray_OnlyReturnsMatching()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "first"),
            CreateRaster(200, "second"),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectIds"] = "[200]",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Features.Should().HaveCount(1);
        jsonResult.Value.Features[0].Attributes["OBJECTID"].Should().Be(200L);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_WhereClause_FiltersByObjectId()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "first"),
            CreateRaster(200, "second"),
            CreateRaster(300, "third"),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["where"] = "OBJECTID >= 200",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Features.Should().HaveCount(2);
        jsonResult.Value.Features.Select(f => f.Attributes["OBJECTID"])
            .Should().BeEquivalentTo([200L, 300L]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_WhereClause_FiltersByBandCountAndPixelType()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "single-band"),
            CreateRaster(200, "multi-band", bandCount: 3, pixelType: "16BUI"),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["where"] = "BandCount = 3 AND PixelType = '16BUI'",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Features.Should().HaveCount(1);
        jsonResult.Value.Features[0].Attributes["OBJECTID"].Should().Be(200L);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_WhereClauseUnknownField_ReturnsBadRequest()
    {
        SetupLayerWithRasters([CreateRaster(100, "first")]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["where"] = "BogusField = 1",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_WhereClauseTooLong_ReturnsBadRequest()
    {
        SetupLayerWithRasters([CreateRaster(100, "first")]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["where"] = new string('x', 2001),
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_NegativeOffset_ReturnsBadRequest()
    {
        SetupLayerWithRasters([CreateRaster(100, "first")]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["resultOffset"] = "-1",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Operation(Operations.Pagination)]
    public async Task QueryCatalogAsync_PaginationLimitsResults()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "first"),
            CreateRaster(200, "second"),
            CreateRaster(300, "third"),
            CreateRaster(400, "fourth"),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["resultOffset"] = "1",
            ["resultRecordCount"] = "2",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Features.Should().HaveCount(2);
        jsonResult.Value.Features.Select(f => f.Attributes["OBJECTID"])
            .Should().BeEquivalentTo([200L, 300L]);
        jsonResult.Value.ExceededTransferLimit.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_UnsupportedFormat_ReturnsBadRequest()
    {
        SetupLayerWithRasters([CreateRaster(100, "first")]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["f"] = "html",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_FeatureGeometrySpatialReference_MatchesRasterNativeSrid()
    {
        // Regression: previously the geometry SR was stamped with outSR even though the
        // MVP does not reproject. The geometry SR must match the actual ring coordinates,
        // which come straight from the raster's native SRID (3857 here).
        SetupLayerWithRasters([CreateRaster(100, "first", srid: 3857)]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["outSR"] = "4326",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogQueryResponse>;
        jsonResult.Should().NotBeNull();
        var feature = jsonResult!.Value!.Features[0];
        feature.Geometry.Should().NotBeNull();
        feature.Geometry!.SpatialReference.Wkid.Should().Be(3857);
        feature.Geometry.SpatialReference.LatestWkid.Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ReturnExtentOnly_IgnoresOutSrWhenNoReprojectionOccurs()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "first", xMin: -10, yMin: -5, xMax: 10, yMax: 5, srid: 3857),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["returnExtentOnly"] = "true",
            ["outSR"] = "4326",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogExtentResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Extent.SpatialReference.Wkid.Should().Be(3857);
        jsonResult.Value.Extent.SpatialReference.LatestWkid.Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ReturnExtentOnlyWithObjectIdsFilter_ShrinksAggregateExtent()
    {
        // Regression: aggregate extent previously iterated the unfiltered raster set,
        // returning the full layer extent regardless of objectIds/where filters.
        SetupLayerWithRasters([
            CreateRaster(100, "west", xMin: -10, yMin: -5, xMax: 0, yMax: 5),
            CreateRaster(200, "east", xMin: 0, yMin: -5, xMax: 10, yMax: 5),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectIds"] = "200",
            ["returnExtentOnly"] = "true",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogExtentResponse>;
        jsonResult.Should().NotBeNull();
        // Only the eastern raster should contribute - aggregate XMin must be 0, not -10.
        jsonResult!.Value!.Extent.XMin.Should().Be(0);
        jsonResult.Value.Extent.XMax.Should().Be(10);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_ReturnExtentOnlyWithWhereFilter_ShrinksAggregateExtent()
    {
        SetupLayerWithRasters([
            CreateRaster(100, "west", xMin: -10, yMin: -5, xMax: 0, yMax: 5),
            CreateRaster(200, "east", xMin: 0, yMin: -5, xMax: 10, yMax: 5),
        ]);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["where"] = "OBJECTID = 100",
            ["returnExtentOnly"] = "true",
        };

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<CatalogExtentResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Extent.XMin.Should().Be(-10);
        jsonResult.Value.Extent.XMax.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task QueryCatalogAsync_RasterStoreThrows_ReturnsServerError()
    {
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns<Task<RasterInfo[]>>(_ => throw new InvalidOperationException("boom"));

        var context = CreateImageServerContext();
        var result = await _handler.QueryCatalogAsync(context, 1, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private void SetupLayerWithRasters(RasterInfo[] rasters)
    {
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(rasters);
    }

    private static Dictionary<string, StringValues> EmptyValues()
        => new(StringComparer.OrdinalIgnoreCase);

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/query";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static TestMetadataV2GraphProvider BuildGraphWithLayer(int layerIndex)
        => new TestMetadataV2GraphBuilder()
            .AddResource($"resource-{layerIndex}", "test-layer", MetadataV2ResourceType.RasterDataset)
            .AddService($"service-{layerIndex}", $"image-svc-{layerIndex}", MetadataV2ServiceType.EsriImageService)
            .AddPublication(
                $"publication-{layerIndex}",
                $"service-{layerIndex}",
                $"resource-{layerIndex}",
                layerIndex: layerIndex,
                serviceLocalId: "test-layer",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();

    private static RasterInfo CreateRaster(
        long id,
        string name,
        double xMin = -1,
        double yMin = -1,
        double xMax = 1,
        double yMax = 1,
        int srid = 4326,
        int bandCount = 1,
        string pixelType = "8BUI") => new()
        {
            Id = id,
            LayerId = 1,
            Name = name,
            Width = 256,
            Height = 256,
            BandCount = bandCount,
            PixelType = pixelType,
            Srid = srid,
            GeoTransform = [xMin, (xMax - xMin) / 256, 0, yMax, 0, -(yMax - yMin) / 256],
            Extent = new RasterExtent { XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax, Srid = srid },
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
}
