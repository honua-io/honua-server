// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
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
using Honua.Infrastructure.Services;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
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
        AssertStructuredContentMatchesOutputSchema(
            new QueryFeaturesTool(_jobService, NullLogger<QueryFeaturesTool>.Instance)
                .Describe()
                .OutputSchema!.Value,
            structured,
            "query_features success structuredContent must validate against its advertised outputSchema");

        structured.GetProperty("returnedCount").GetInt32().Should().Be(1);
        var feature = structured.GetProperty("geojson").GetProperty("features")[0];
        feature.GetProperty("type").GetString().Should().Be("Feature");
        feature.GetProperty("id").GetInt64().Should().Be(7);
        feature.GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
        feature.GetProperty("properties").GetProperty("name").GetString().Should().Be("Lot 7");
        var featureAlias = structured.GetProperty("features")[0];
        featureAlias.GetProperty("id").GetInt64().Should().Be(7);
        featureAlias.GetProperty("attributes").GetProperty("name").GetString().Should().Be("Lot 7");

        await reader.Received(1).QueryAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public void Describe_QueryFeatures_AdvertisesPagingAndCountParameters()
    {
        var descriptor = new QueryFeaturesTool(_jobService, NullLogger<QueryFeaturesTool>.Instance).Describe();

        var inputProps = descriptor.InputSchema.GetProperty("properties");
        inputProps.TryGetProperty("resultOffset", out var resultOffset).Should().BeTrue();
        resultOffset.GetProperty("type").GetString().Should().Be("integer");
        resultOffset.GetProperty("minimum").GetInt32().Should().Be(0);
        inputProps.TryGetProperty("cursor", out var cursor).Should().BeTrue();
        cursor.GetProperty("type").GetString().Should().Be("string");
        inputProps.TryGetProperty("returnGeometry", out var returnGeometry).Should().BeTrue();
        returnGeometry.GetProperty("type").GetString().Should().Be("boolean");
        inputProps.TryGetProperty("returnCountOnly", out var returnCountOnly).Should().BeTrue();
        returnCountOnly.GetProperty("type").GetString().Should().Be("boolean");

        descriptor.OutputSchema.Should().NotBeNull();
        var outputProps = descriptor.OutputSchema!.Value.GetProperty("properties");
        outputProps.TryGetProperty("nextOffset", out _).Should().BeTrue();
        outputProps.TryGetProperty("nextCursor", out _).Should().BeTrue();
        outputProps.TryGetProperty("features", out _).Should().BeTrue();
        outputProps.TryGetProperty("count", out _).Should().BeTrue();
        outputProps.TryGetProperty("resultOffset", out _).Should().BeTrue();

        descriptor.Description.Should().Contain("nextOffset",
            "the tool description must teach the mechanical paging loop");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_QueryFeatures_WithResultOffset_PagesDisjointResultsUsingNextOffset()
    {
        // A 250-row layer paged at limit=100 must be traversed in exactly three
        // calls: offset 0 -> 100 -> 200, each page disjoint, driven only by the
        // returned nextOffset until exceededTransferLimit flips false.
        const int total = 250;
        const int pageSize = 100;

        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var query = callInfo.ArgAt<FeatureQuery>(1);
                var offset = query.ResultOffset ?? 0;
                var limit = query.Limit ?? pageSize;
                var take = Math.Min(limit, Math.Max(0, total - offset));
                var items = Enumerable.Range(offset, take)
                    .Select(id => new Feature
                    {
                        Id = id,
                        Geometry = [0x01],
                        Attributes = ImmutableDictionary<string, object?>.Empty.Add("idx", id)
                    })
                    .ToImmutableArray();
                return new QueryResult<Feature>
                {
                    TotalCount = total,
                    HasMoreResults = offset + take < total,
                    Items = items
                };
            });

        var geometryService = Substitute.For<IGeometryService>();
        geometryService.ConvertWkbToGeoJson(Arg.Any<byte[]?>())
            .Returns("""{"type":"Point","coordinates":[1,2]}""");

        var surface = BuildSurface();
        var services = BuildServices(reader: reader, geometryService: geometryService);

        var seenIds = new List<long>();
        string? cursorArg = null;
        var calls = 0;

        while (true)
        {
            calls++;
            var cursorJson = cursorArg is { } cursor ? $",\"cursor\":\"{cursor}\"" : string.Empty;
            var response = await surface.DispatchAsync(
                AuthenticatedContext(services),
                ToolCall($"page-{calls}", QueryFeaturesTool.ToolName, $$"""
                    {"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"limit":{{pageSize}}{{cursorJson}}}
                    """),
                CancellationToken.None);

            response!.Error.Should().BeNull();
            var structured = response.Result!.Value.GetProperty("structuredContent");

            foreach (var feature in structured.GetProperty("geojson").GetProperty("features").EnumerateArray())
            {
                seenIds.Add(feature.GetProperty("id").GetInt64());
            }

            if (structured.GetProperty("exceededTransferLimit").GetBoolean())
            {
                structured.TryGetProperty("nextOffset", out var nextOffset).Should().BeTrue(
                    "a non-final page must advertise nextOffset so the agent can page mechanically");
                structured.TryGetProperty("nextCursor", out var nextCursor).Should().BeTrue(
                    "a non-final page must also advertise nextCursor for MCP cursor-style pagination");
                nextCursor.GetString().Should().Be(nextOffset.GetInt32().ToString(CultureInfo.InvariantCulture));
                cursorArg = nextCursor.GetString();
            }
            else
            {
                structured.TryGetProperty("nextOffset", out _).Should().BeFalse(
                    "the final page must not advertise nextOffset");
                structured.TryGetProperty("nextCursor", out _).Should().BeFalse(
                    "the final page must not advertise nextCursor");
                break;
            }

            calls.Should().BeLessThan(10, "paging must terminate");
        }

        calls.Should().Be(3, "a 250-row layer at limit=100 pages in exactly three calls");
        seenIds.Should().HaveCount(total);
        seenIds.Should().OnlyHaveUniqueItems("pages must be disjoint");
        seenIds.Should().BeEquivalentTo(Enumerable.Range(0, total).Select(i => (long)i));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_QueryFeatures_ReturnCountOnly_ReturnsCountWithoutFeatures()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.CountAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(250L);

        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices(reader: reader)),
            ToolCall("count-1", QueryFeaturesTool.ToolName, $$"""
                {"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"returnCountOnly":true}
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        AssertStructuredContentMatchesOutputSchema(
            new QueryFeaturesTool(_jobService, NullLogger<QueryFeaturesTool>.Instance)
                .Describe()
                .OutputSchema!.Value,
            structured,
            "query_features count-only structuredContent must validate against its advertised outputSchema");

        structured.GetProperty("count").GetInt64().Should().Be(250);
        structured.GetProperty("returnedCount").GetInt32().Should().Be(0);
        structured.TryGetProperty("geojson", out _).Should().BeFalse(
            "returnCountOnly must omit the feature collection");

        await reader.Received(1).CountAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        await reader.DidNotReceive().QueryAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_QueryFeatures_InvalidArguments_ReturnsStructuredValidationErrorMatchingOutputSchema()
    {
        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices()),
            ToolCall("query-invalid-1", QueryFeaturesTool.ToolName, $$"""
                {"serviceId":"{{ServiceId}}"}
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        AssertStructuredContentMatchesOutputSchema(
            new QueryFeaturesTool(_jobService, NullLogger<QueryFeaturesTool>.Instance)
                .Describe()
                .OutputSchema!.Value,
            structured,
            "query_features error structuredContent must validate against its advertised outputSchema");

        structured.GetProperty("status").GetString().Should().Be("error");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        var error = structured.GetProperty("error");
        error.GetProperty("kind").GetString().Should().Be("ValidationFailed");
        error.GetProperty("violations").GetArrayLength().Should().BeGreaterThan(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_QueryFeatures_ReturnGeometryFalse_OmitsGeometry()
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

        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices(reader: reader, geometryService: geometryService)),
            ToolCall("nogeom-1", QueryFeaturesTool.ToolName, $$"""
                {"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"returnGeometry":false}
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        structured.GetProperty("returnedCount").GetInt32().Should().Be(1);
        var feature = structured.GetProperty("geojson").GetProperty("features")[0];
        feature.GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null,
            "returnGeometry=false yields attribute-only rows with null geometry");
        feature.GetProperty("properties").GetProperty("name").GetString().Should().Be("Lot 7");

        geometryService.DidNotReceive().ConvertWkbToGeoJson(Arg.Any<byte[]?>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_QueryFeatures_QuantizesGeometryToDefaultPrecision()
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
                        Attributes = ImmutableDictionary<string, object?>.Empty
                    }
                ]
            });

        var geometryService = Substitute.For<IGeometryService>();
        geometryService.ConvertWkbToGeoJson(Arg.Any<byte[]?>())
            .Returns("""{"type":"Point","coordinates":[1.123456789,2.987654321]}""");

        var surface = BuildSurface();

        // Default precision (6 dp) rounds full-precision coordinate noise away.
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices(reader: reader, geometryService: geometryService)),
            ToolCall("quant-1", QueryFeaturesTool.ToolName, $$"""
                {"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}}}
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var coords = response.Result!.Value
            .GetProperty("structuredContent").GetProperty("geojson").GetProperty("features")[0]
            .GetProperty("geometry").GetProperty("coordinates");
        coords[0].GetDouble().Should().Be(1.123457);
        coords[1].GetDouble().Should().Be(2.987654);

        // A negative precision opts back into full, unrounded coordinates.
        var fullResponse = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices(reader: reader, geometryService: geometryService)),
            ToolCall("quant-2", QueryFeaturesTool.ToolName, $$"""
                {"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"geometryPrecision":-1}
                """),
            CancellationToken.None);

        var fullCoords = fullResponse!.Result!.Value
            .GetProperty("structuredContent").GetProperty("geojson").GetProperty("features")[0]
            .GetProperty("geometry").GetProperty("coordinates");
        fullCoords[0].GetDouble().Should().Be(1.123456789);
        fullCoords[1].GetDouble().Should().Be(2.987654321);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_render_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_RenderMap_ByDefault_ReturnsArtifactReferenceNotInlineImage()
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

        const string href = "/temp/abc123.png";
        var temporaryFileService = BuildTemporaryFileService(href);

        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices(renderer: renderer, temporaryFileService: temporaryFileService)),
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
        var structured = result.GetProperty("structuredContent");
        AssertStructuredContentMatchesOutputSchema(
            new RenderMapTool(_jobService, NullLogger<RenderMapTool>.Instance)
                .Describe()
                .OutputSchema!.Value,
            structured,
            "render_map artifact-reference structuredContent must validate against its advertised outputSchema");
        structured.GetProperty("delivery").GetString().Should().Be("resource_link");
        structured.GetProperty("uri").GetString().Should().Be(href);

        // No base64 image block is inlined by default — the context stays clean.
        content.Should().NotContain(block => block.GetProperty("type").GetString() == "image",
            "the default render must not inline a base64 image into the model context");

        var textBlock = content.Single(block => block.GetProperty("type").GetString() == "text");
        textBlock.GetProperty("text").GetString().Should().Contain(href,
            "the text block must carry the fetchable artifact href");

        var linkBlock = content.Single(block => block.GetProperty("type").GetString() == "resource_link");
        linkBlock.GetProperty("uri").GetString().Should().Be(href);
        linkBlock.GetProperty("mimeType").GetString().Should().Be("image/png");

        await temporaryFileService.Received(1).StoreTemporaryFileAsync(
            Arg.Is<byte[]>(b => b.SequenceEqual(pngBytes)),
            "image/png",
            Arg.Any<TimeSpan?>(),
            Arg.Any<System.Security.Claims.ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_render_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_RenderMap_WithMaxInlineBytes_InlinesBase64Image()
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

        var temporaryFileService = BuildTemporaryFileService();

        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices(renderer: renderer, temporaryFileService: temporaryFileService)),
            ToolCall("render-inline-1", RenderMapTool.ToolName, $$"""
                {
                  "layers":[{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}}}],
                  "bbox":[-10,-10,10,10],
                  "width":256,
                  "height":256,
                  "maxInlineBytes":1048576
                }
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var content = result.GetProperty("content").EnumerateArray().ToArray();
        var structured = result.GetProperty("structuredContent");
        AssertStructuredContentMatchesOutputSchema(
            new RenderMapTool(_jobService, NullLogger<RenderMapTool>.Instance)
                .Describe()
                .OutputSchema!.Value,
            structured,
            "render_map inline structuredContent must validate against its advertised outputSchema");
        structured.GetProperty("delivery").GetString().Should().Be("inline");
        structured.TryGetProperty("uri", out _).Should().BeFalse();
        content.Should().Contain(block => block.GetProperty("type").GetString() == "text");

        var imageBlock = content.Single(block => block.GetProperty("type").GetString() == "image");
        imageBlock.GetProperty("mimeType").GetString().Should().Be("image/png");
        var data = imageBlock.GetProperty("data").GetString();
        data.Should().NotBeNullOrEmpty();
        Convert.FromBase64String(data!).Should().Equal(pngBytes);

        // The small image fit under maxInlineBytes, so no temp artifact was created.
        await temporaryFileService.DidNotReceive().StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<System.Security.Claims.ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>());

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
        IRasterMapRenderer? renderer = null,
        ITemporaryFileService? temporaryFileService = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(BuildGraphProvider());
        services.AddSingleton(reader ?? Substitute.For<IFeatureReader>());
        services.AddSingleton(geometryService ?? Substitute.For<IGeometryService>());
        services.AddSingleton(renderer ?? Substitute.For<IRasterMapRenderer>());
        services.AddSingleton(Substitute.For<IFilterExpressionService>());
        services.AddSingleton(temporaryFileService ?? BuildTemporaryFileService());
        return services.BuildServiceProvider();
    }

    private static ITemporaryFileService BuildTemporaryFileService(string href = "/temp/rendered-map.png")
    {
        var service = Substitute.For<ITemporaryFileService>();
        service
            .StoreTemporaryFileAsync(
                Arg.Any<byte[]>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<System.Security.Claims.ClaimsPrincipal?>(),
                Arg.Any<CancellationToken>())
            .Returns(href);
        return service;
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

    private static void AssertStructuredContentMatchesOutputSchema(
        JsonElement outputSchema,
        JsonElement structuredContent,
        string because)
    {
        var schema = JSchema.Parse(outputSchema.GetRawText());
        var payload = JToken.Parse(structuredContent.GetRawText());
        payload.IsValid(schema, out IList<string> errors).Should().BeTrue(
            because + ": " + string.Join("; ", errors));
    }
}
