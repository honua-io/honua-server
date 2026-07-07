// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
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
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the styling MCP tools (<c>honua_get_style</c> /
/// <c>honua_apply_style_preset</c>). Both are thin adapters over the canonical
/// styleId-keyed <see cref="IStyleCatalog"/> and the Metadata v2 style graph
/// (ADR-0048), so these tests substitute those seams and assert the adapters
/// read/bind styles correctly, that an unknown preset is rejected naming the
/// valid presets, that applying is gated on the authoring grant, and that a
/// subsequent <c>honua_render_map</c> resolves the applied style.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpStyleToolTests
{
    private const string ServiceId = "svc-parcels";
    private const string ServiceName = "Parcels";
    private const string ResourceId = "res-parcels";
    private const int LayerIndex = 0;
    private const int StorageLayerId = 42;
    private const string PresetStyleId = "style_flood_depth";

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    private static StyleCatalogRecord Preset(string styleId = PresetStyleId, int version = 3) => new()
    {
        StyleId = styleId,
        Title = "Flood depth",
        Description = "Graduated flood depth ramp.",
        MapLibreStyleJson = "{\"version\":8,\"layers\":[]}",
        StyleVersion = version
    };

    // ---------------------------------------------------------------
    // honua_get_style
    // ---------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_style")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GetStyle_ByStyleId_ReturnsStyleRefWithInlinedStylesheet()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());

        var response = await DispatchAsync(
            GetStyleTool.ToolName,
            $$"""{ "styleId": "{{PresetStyleId}}", "encoding": "mapbox-style", "includeStylesheet": true }""",
            catalog: catalog);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        structured.GetProperty("styleId").GetString().Should().Be(PresetStyleId);
        structured.GetProperty("styleVersion").GetInt32().Should().Be(3);

        var encodings = structured.GetProperty("encodings").EnumerateArray().ToArray();
        encodings.Should().Contain(e => e.GetProperty("encoding").GetString() == "mapbox-style");
        var mapbox = encodings.Single(e => e.GetProperty("encoding").GetString() == "mapbox-style");
        // includeStylesheet inlines the canonical MapLibre body for the selected encoding.
        mapbox.GetProperty("inlineBody").GetString().Should().Contain("\"version\":8");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_style")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GetStyle_ByLayer_ResolvesLayerPrimaryStyle()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStylesForLayerAsync(StorageLayerId, Arg.Any<CancellationToken>())
            .Returns(new[] { Preset() });
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());

        var response = await DispatchAsync(
            GetStyleTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}} }""",
            catalog: catalog);

        response!.Error.Should().BeNull();
        response.Result!.Value.GetProperty("structuredContent").GetProperty("styleId").GetString()
            .Should().Be(PresetStyleId);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_style")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GetStyle_NoArguments_ListsAvailableStyles()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.ListStylesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Preset(), Preset("style_parcels", 1) });

        var response = await DispatchAsync(GetStyleTool.ToolName, "{}", catalog: catalog);

        response!.Error.Should().BeNull();
        var styles = response.Result!.Value.GetProperty("structuredContent")
            .GetProperty("styles").EnumerateArray().ToArray();
        styles.Should().HaveCount(2);
        styles.Select(s => s.GetProperty("styleId").GetString())
            .Should().BeEquivalentTo(PresetStyleId, "style_parcels");
        styles[0].GetProperty("uri").GetString().Should().Be($"honua://styles/{PresetStyleId}");
    }

    // ---------------------------------------------------------------
    // honua_apply_style_preset
    // ---------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_BindsPresetAndSyncsGraph()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        catalog.AssociateLayerAsync(StorageLayerId, PresetStyleId, 0, Arg.Any<CancellationToken>()).Returns(true);
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();

        var response = await DispatchAsync(
            ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "{{PresetStyleId}}" }""",
            catalog: catalog,
            graphSync: graphSync);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        structured.GetProperty("styleId").GetString().Should().Be(PresetStyleId);
        structured.GetProperty("applied").GetBoolean().Should().BeTrue();
        structured.GetProperty("layerId").GetInt32().Should().Be(LayerIndex);

        // The preset was bound as the layer's primary style and the graph reconciled.
        await catalog.Received(1).AssociateLayerAsync(StorageLayerId, PresetStyleId, 0, Arg.Any<CancellationToken>());
        await graphSync.Received(1).SyncLayerStylesAsync(StorageLayerId, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_UnknownPreset_ReturnsInvalidArgumentNamingValidPresets()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync("style_missing", Arg.Any<CancellationToken>()).Returns((StyleCatalogRecord?)null);
        catalog.ListStylesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Preset(), Preset("style_parcels", 1) });
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();

        var response = await DispatchAsync(
            ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "style_missing" }""",
            catalog: catalog,
            graphSync: graphSync);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should()
            .Contain("style_missing").And.Contain(PresetStyleId).And.Contain("style_parcels");

        // No binding or graph mutation happened for an unknown preset.
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_QueryOnlyPrincipal_ReturnsPermissionDenied()
    {
        // A query-only principal holds Read/Discover grants but not the
        // PublishedService.Publish authoring grant apply_style_preset requires.
        _jobService
            .EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                OperatorResourceType.PublishedService,
                OperatorOperation.Publish,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new GeoprocessingAuthorizationException(requiresAuthentication: false)));

        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();

        var response = await DispatchAsync(
            ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "{{PresetStyleId}}" }""",
            catalog: catalog,
            graphSync: graphSync);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("permission_denied");

        // Authorization is enforced before any style binding is written.
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    // ---------------------------------------------------------------
    // render reflects the applied style (mock-level)
    // ---------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_render_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_RenderMap_ReflectsAppliedStyleInCaption()
    {
        // After apply_style_preset, the layer's primary style resolves to the
        // preset; render_map surfaces it so the applied style is observable.
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStylesForLayerAsync(StorageLayerId, Arg.Any<CancellationToken>())
            .Returns(new[] { Preset() });

        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = Encoding.ASCII.GetBytes("PNGDATA"),
                ContentType = "image/png",
                Width = 256,
                Height = 256
            });

        var response = await DispatchAsync(
            RenderMapTool.ToolName,
            $$"""{ "layers":[{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}}}], "bbox":[-10,-10,10,10] }""",
            catalog: catalog,
            renderer: renderer);

        response!.Error.Should().BeNull();
        var content = response.Result!.Value.GetProperty("content").EnumerateArray().ToArray();
        var caption = content.First(b => b.GetProperty("type").GetString() == "text").GetProperty("text").GetString();
        caption.Should().Contain(PresetStyleId, "render_map reports each layer's effective (applied) style");
    }

    // ---------------------------------------------------------------
    // harness
    // ---------------------------------------------------------------

    private async Task<McpJsonRpcResponse?> DispatchAsync(
        string toolName,
        string argumentsJson,
        IStyleCatalog? catalog = null,
        IMetadataV2StyleGraphSync? graphSync = null,
        IRasterMapRenderer? renderer = null)
    {
        var surface = new McpOperatorSurface(
            [
                new GetStyleTool(_jobService, NullLogger<GetStyleTool>.Instance),
                new ApplyStylePresetTool(_jobService, NullLogger<ApplyStylePresetTool>.Instance),
                new RenderMapTool(_jobService, NullLogger<RenderMapTool>.Instance),
            ],
            [],
            NullLogger<McpOperatorSurface>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(BuildGraphProvider());
        services.AddSingleton(catalog ?? Substitute.For<IStyleCatalog>());
        services.AddSingleton(graphSync ?? Substitute.For<IMetadataV2StyleGraphSync>());
        services.AddSingleton(renderer ?? Substitute.For<IRasterMapRenderer>());

        // render_map's default result is an artifact reference stored through the
        // shared temp-file pipeline; stub it so the href path resolves in tests.
        var temporaryFileService = Substitute.For<ITemporaryFileService>();
        temporaryFileService
            .StoreTemporaryFileAsync(
                Arg.Any<byte[]>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<ClaimsPrincipal?>(),
                Arg.Any<CancellationToken>())
            .Returns("/temp/rendered-map.png");
        services.AddSingleton(temporaryFileService);

        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services.BuildServiceProvider();

        return await surface.DispatchAsync(context, ToolCall("style-1", toolName, argumentsJson), CancellationToken.None);
    }

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
