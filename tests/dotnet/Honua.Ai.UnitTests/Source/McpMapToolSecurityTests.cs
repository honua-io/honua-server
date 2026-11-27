// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class McpMapToolSecurityTests
{
    private const string ServiceId = "svc-parcels";
    private const string ResourceId = "res-parcels";
    private const int LayerIndex = 0;
    private const int StorageLayerId = 42;

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_describe_layer")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_DescribeLayer_WithFieldMasks_ExcludesMaskedFields()
    {
        var maskSource = Substitute.For<IFieldMaskSource>();
        maskSource.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("area_sq_m"));

        using var services = BuildServices(new SecurityTestServices { FieldMaskSource = maskSource });
        var response = await BuildSurface().DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("describe-masked", DescribeLayerTool.ToolName, $$"""
                {"serviceId":"{{ServiceId}}","serviceLayerIndex":{{LayerIndex}},"includeRowCount":false}
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        structured.GetProperty("fieldCount").GetInt32().Should().Be(2);
        var fieldNames = structured.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).ToArray();
        fieldNames.Should().Equal("objectid", "name");
        fieldNames.Should().NotContain("area_sq_m");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_render_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_RenderMap_MaxInlineBytesAboveServerCeiling_FallsBackToResourceLink()
    {
        var oversizedPng = new byte[MapToolSchemas.MaxInlineBytesCeiling + 1];
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = oversizedPng,
                ContentType = "image/png",
                Width = 512,
                Height = 512
            });
        var temporaryFileService = Substitute.For<ITemporaryFileService>();
        temporaryFileService.StoreTemporaryFileAsync(
                Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(),
                Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>())
            .Returns("honua://artifacts/oversized.png");

        using var services = BuildServices(new SecurityTestServices
        {
            Renderer = renderer,
            TemporaryFileService = temporaryFileService
        });
        var response = await BuildSurface().DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("render-clamped", RenderMapTool.ToolName, $$"""
                {
                  "layers":[{"serviceId":"{{ServiceId}}","serviceLayerIndex":{{LayerIndex}}}],
                  "bbox":[-10,-10,10,10],
                  "maxInlineBytes":10485760
                }
                """),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("structuredContent").GetProperty("image").GetProperty("inlined").GetBoolean().Should().BeFalse();
        result.GetProperty("content").EnumerateArray()
            .Should().NotContain(block => block.GetProperty("type").GetString() == "image");
    }

    private McpDataAccessSurface BuildSurface() => new(
        [
            new DescribeLayerTool(_jobService, NullLogger<DescribeLayerTool>.Instance),
            new RenderMapTool(_jobService, NullLogger<RenderMapTool>.Instance)
        ],
        [],
        NullLogger<McpDataAccessSurface>.Instance);

    private static ServiceProvider BuildServices(SecurityTestServices options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(BuildGraphProvider());
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddOptions<RbacOptions>();
        services.AddSingleton(Substitute.For<IFeatureReader>());
        services.AddSingleton(options.Renderer ?? Substitute.For<IRasterMapRenderer>());
        services.AddSingleton(options.TemporaryFileService ?? Substitute.For<ITemporaryFileService>());
        if (options.FieldMaskSource is not null)
        {
            services.AddSingleton(options.FieldMaskSource);
        }

        return services.BuildServiceProvider();
    }

    private static TestMetadataV2GraphProvider BuildGraphProvider()
    {
        var spatial = new MetadataV2ObjectSpatial
        {
            GeometryType = MetadataV2GeometryType.Polygon,
            SpatialReference = new MetadataV2SpatialReference { Srid = 4326 },
            Bbox = new MetadataV2Bbox { West = -10, South = -10, East = 10, North = 10 }
        };

        return new TestMetadataV2GraphBuilder()
            .AddResource(
                ResourceId,
                "Parcels Dataset",
                spatial: spatial,
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Alias = "OBJECTID" },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Alias = "Name" },
                    new MetadataV2Field { Name = "area_sq_m", Type = MetadataV2FieldType.Double, Nullable = true }
                ])
            .AddStorageBinding("bind-parcels", ResourceId, "public.parcels", storageLayerId: StorageLayerId)
            .AddService(ServiceId, "Parcels")
            .AddPublication("pub-parcels", ServiceId, ResourceId, layerIndex: LayerIndex, storageBindingId: "bind-parcels")
            .BuildProvider();
    }

    private static DefaultHttpContext AuthenticatedContext(IServiceProvider services)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        return context;
    }

    private static McpJsonRpcRequest ToolCall(string id, string toolName, string argumentsJson)
    {
        using var idDoc = JsonDocument.Parse(JsonSerializer.Serialize(id));
        using var paramsDoc = JsonDocument.Parse($$"""{"name":"{{toolName}}","arguments":{{argumentsJson}}}""");
        return new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = idDoc.RootElement.Clone(),
            Method = "tools/call",
            Params = paramsDoc.RootElement.Clone()
        };
    }

    private sealed class SecurityTestServices
    {
        public IFieldMaskSource? FieldMaskSource { get; init; }
        public IRasterMapRenderer? Renderer { get; init; }
        public ITemporaryFileService? TemporaryFileService { get; init; }
    }
}
