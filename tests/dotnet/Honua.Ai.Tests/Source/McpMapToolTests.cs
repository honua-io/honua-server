// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Queries.Filters;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the catalog-discovery, feature-query, and map-render MCP
/// tools. These run through the JSON-RPC dispatcher while substituting the
/// canonical metadata / feature-query / raster-render services, so they validate
/// the MCP adapter behavior without a database or renderer dependency.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpMapToolTests
{
    private const string ServiceId = "svc-parcels";
    private const string ServiceName = "Parcels";
    private const string ResourceId = "res-parcels";
    private const int LayerIndex = 0;
    private const int StorageLayerId = 42;

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task ToolsList_IncludesCatalogQueryAndRenderTools()
    {
        var surface = BuildSurface();

        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices()),
            ListToolsRequest("list-1"),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var names = response.Result!.Value.GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToArray();

        names.Should().Contain([
            ListLayersTool.ToolName,
            QueryFeaturesTool.ToolName,
            RenderMapTool.ToolName
        ]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_list_layers")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ListLayers_ReturnsPublishedLayers()
    {
        var surface = BuildSurface();

        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices()),
            ToolCall("layers-1", ListLayersTool.ToolName, "{}"),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("layerCount").GetInt32().Should().Be(1);
        var layer = structured.GetProperty("layers")[0];
        layer.GetProperty("serviceId").GetString().Should().Be(ServiceId);
        layer.GetProperty("layerId").GetInt32().Should().Be(LayerIndex);
        layer.GetProperty("geometryType").GetString().Should().Be(nameof(MetadataV2GeometryType.Polygon));
        layer.GetProperty("extent").GetProperty("minX").GetDouble().Should().Be(-10);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_QueryFeatures_ReturnsGeoJson()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResult<Feature>
            {
                TotalCount = 1,
                HasMoreResults = false,
                Items =
                [
                    new Feature
                    {
                        Id = 7,
                        Geometry = [0x01],
                        Attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "Lot 7")
                    }
                ]
            });

        var geometryService = Substitute.For<IGeometryService>();
        geometryService.ConvertWkbToGeoJson(Arg.Any<byte[]?>())
            .Returns("""{"type":"Point","coordinates":[1,2]}""");

        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices(reader: reader, geometryService: geometryService)),
            ToolCall("query-1", QueryFeaturesTool.ToolName, $$"""
                {"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"limit":10}
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("returnedCount").GetInt32().Should().Be(1);
        var feature = structured.GetProperty("geojson").GetProperty("features")[0];
        feature.GetProperty("type").GetString().Should().Be("Feature");
        feature.GetProperty("id").GetInt64().Should().Be(7);
        feature.GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
        feature.GetProperty("properties").GetProperty("name").GetString().Should().Be("Lot 7");

        await reader.Received(1).QueryAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_render_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_RenderMap_ReturnsImageContentBlock()
    {
        var pngBytes = Encoding.ASCII.GetBytes("PNGDATA");
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(
                Arg.Is<int[]>(ids => ids.Length == 1 && ids[0] == StorageLayerId),
                Arg.Any<MapRenderRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = pngBytes,
                ContentType = "image/png",
                Width = 256,
                Height = 256
            });

        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices(renderer: renderer)),
            ToolCall("render-1", RenderMapTool.ToolName, $$"""
                {
                  "layers":[{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}}}],
                  "bbox":[-10,-10,10,10],
                  "width":256,
                  "height":256
                }
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var content = result.GetProperty("content").EnumerateArray().ToArray();
        content.Should().Contain(block => block.GetProperty("type").GetString() == "text");

        var imageBlock = content.Single(block => block.GetProperty("type").GetString() == "image");
        imageBlock.GetProperty("mimeType").GetString().Should().Be("image/png");
        var data = imageBlock.GetProperty("data").GetString();
        data.Should().NotBeNullOrEmpty();
        Convert.FromBase64String(data!).Should().Equal(pngBytes);

        await renderer.Received(1).RenderDatasetMapAsync(
            Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>());
    }

    private McpOperatorSurface BuildSurface() => new(
        [
            new ListLayersTool(_jobService, NullLogger<ListLayersTool>.Instance),
            new QueryFeaturesTool(_jobService, NullLogger<QueryFeaturesTool>.Instance),
            new RenderMapTool(_jobService, NullLogger<RenderMapTool>.Instance)
        ],
        [],
        NullLogger<McpOperatorSurface>.Instance);

    private static TestMetadataV2GraphProvider BuildGraphProvider()
    {
        var spatial = new MetadataV2ResourceSpatial
        {
            GeometryType = MetadataV2GeometryType.Polygon,
            SpatialReference = new MetadataV2SpatialReference { Srid = 4326 },
            Bbox = new MetadataV2Bbox { West = -10, South = -10, East = 10, North = 10 }
        };

        return new TestMetadataV2GraphBuilder()
            .AddResource(ResourceId, "Parcels Dataset", spatial: spatial)
            .AddStorageBinding("bind-parcels", ResourceId, "public.parcels", storageLayerId: StorageLayerId)
            .AddService(ServiceId, ServiceName)
            .AddPublication("pub-parcels", ServiceId, ResourceId, layerIndex: LayerIndex, storageBindingId: "bind-parcels")
            .BuildProvider();
    }

    private static ServiceProvider BuildServices(
        IFeatureReader? reader = null,
        IGeometryService? geometryService = null,
        IRasterMapRenderer? renderer = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(BuildGraphProvider());
        services.AddSingleton(reader ?? Substitute.For<IFeatureReader>());
        services.AddSingleton(geometryService ?? Substitute.For<IGeometryService>());
        services.AddSingleton(renderer ?? Substitute.For<IRasterMapRenderer>());
        services.AddSingleton(Substitute.For<IFilterExpressionService>());
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext AuthenticatedContext(IServiceProvider services)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        return context;
    }

    private static McpJsonRpcRequest ListToolsRequest(string id) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/list"
    };

    private static McpJsonRpcRequest ToolCall(string id, string toolName, string argumentsJson) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/call",
        Params = Json($$"""
            {"name":"{{toolName}}","arguments":{{argumentsJson}}}
            """)
    };

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
