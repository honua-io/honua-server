// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Geoprocessing;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Hunt-only cross-surface authorization probes for the shared layer read operation.
///
/// REST FeatureServer and gRPC FeatureService evaluate the canonical layer AccessPolicy
/// before reading a layer. The MCP map tools currently check only coarse Process.Read and
/// then resolve the caller-supplied service/layer tuple. These probes use the same restricted
/// Metadata v2 resource and the same analyst principal and must therefore return the same
/// denial. They are intentionally red on the current implementation: this file records the
/// finding and contains no production fix.
/// </summary>
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed class AuthzConsistencyHuntTests
{
    private const string ServiceId = "svc-private";
    private const string ServiceName = "Private parcels";
    private const string ResourceId = "res-private-parcels";
    private const int LayerId = 0;
    private const int StorageLayerId = 700;

    public static TheoryData<string> RestrictedReadTools => new()
    {
        ListLayersTool.ToolName,
        DescribeLayerTool.ToolName,
        QueryFeaturesTool.ToolName,
        RenderMapTool.ToolName
    };

    [Theory]
    [MemberData(nameof(RestrictedReadTools))]
    [Endpoint("POST /mcp tools/call restricted layer read matrix")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task RestrictedLayer_ReadMustBeDeniedForTheSamePrincipalAcrossRestGrpcAndMcp(
        string toolName)
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
                        Id = 91,
                        Geometry = [0x01],
                        Attributes = ImmutableDictionary<string, object?>.Empty
                            .Add("owner", "tenant-b")
                    }
                ]
            });

        reader.CountAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(1L);

        var geometryService = Substitute.For<IGeometryService>();
        geometryService.ConvertWkbToGeoJson(Arg.Any<byte[]?>())
            .Returns("{\"type\":\"Point\",\"coordinates\":[1,2]}");

        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(
                Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x89, 0x50, 0x4E, 0x47],
                ContentType = "image/png",
                Width = 1,
                Height = 1
            });

        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                OperatorResourceType.Process,
                OperatorOperation.Read,
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(BuildRestrictedGraphProvider())
            .AddSingleton<IFeatureReader>(reader)
            .AddSingleton<IGeometryService>(geometryService)
            .AddSingleton<IFilterExpressionService>(Substitute.For<IFilterExpressionService>())
            .AddSingleton<IRasterMapRenderer>(renderer)
            .AddSingleton<ITemporaryFileService>(BuildTemporaryFileService())
            .BuildServiceProvider();

        var response = await BuildSurface(jobService).DispatchAsync(
            AnalystContext(services),
            ToolCall(toolName),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;

        // This is the expected parity result: the same restricted resource is denied by the
        // REST/gRPC resource-access seam. Current MCP code returns success for all four rows.
        result.GetProperty("isError").GetBoolean().Should().BeTrue(
            "an analyst without the layer-reader grant must not read or discover this layer through MCP");
        result.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be("permission_denied");

        await reader.DidNotReceiveWithAnyArgs()
            .QueryAsync(default, default!, default);
        await reader.DidNotReceiveWithAnyArgs()
            .CountAsync(default, default!, default);
        await renderer.DidNotReceiveWithAnyArgs()
            .RenderDatasetMapAsync(default!, default!, default);
    }

    private static McpDataAccessSurface BuildSurface(IGeoprocessingJobService jobService)
        => new(
        [
            new ListLayersTool(jobService, NullLogger<ListLayersTool>.Instance),
            new DescribeLayerTool(jobService, NullLogger<DescribeLayerTool>.Instance),
            new QueryFeaturesTool(jobService, NullLogger<QueryFeaturesTool>.Instance),
            new RenderMapTool(jobService, NullLogger<RenderMapTool>.Instance)
        ],
        [],
        NullLogger<McpDataAccessSurface>.Instance);

    private static DefaultHttpContext AnalystContext(IServiceProvider services)
        => new()
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "tenant-a-user"),
                new Claim(ClaimTypes.Role, "analyst"),
                new Claim("tenant_id", "tenant-a")
            ], "OAuth"))
        };

    private static McpJsonRpcRequest ToolCall(string toolName)
    {
        var arguments = toolName switch
        {
            ListLayersTool.ToolName => "{}",
            DescribeLayerTool.ToolName =>
                $"{{\"serviceId\":\"{ServiceId}\",\"layerId\":{LayerId},\"includeRowCount\":true}}",
            QueryFeaturesTool.ToolName =>
                $"{{\"serviceId\":\"{ServiceId}\",\"layerId\":{LayerId}}}",
            RenderMapTool.ToolName =>
                $"{{\"layers\":[{{\"serviceId\":\"{ServiceId}\",\"layerId\":{LayerId}}}],\"bbox\":[-10,-10,10,10]}}",
            _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, null)
        };

        using var id = JsonDocument.Parse("\"authz-hunt\"");
        using var parameters = JsonDocument.Parse(
            $"{{\"name\":{JsonSerializer.Serialize(toolName)},\"arguments\":{arguments}}}");

        return new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = id.RootElement.Clone(),
            Method = "tools/call",
            Params = parameters.RootElement.Clone()
        };
    }

    private static TestMetadataV2GraphProvider BuildRestrictedGraphProvider()
    {
        var spatial = new MetadataV2ResourceSpatial
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReference = new MetadataV2SpatialReference { Srid = 4326 },
            Bbox = new MetadataV2Bbox { West = -10, South = -10, East = 10, North = 10 }
        };

        return new TestMetadataV2GraphBuilder()
            .AddResource(
                ResourceId,
                "Private parcels",
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                    new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String, Nullable = false }
                ],
                accessPolicy: new AccessPolicy { AllowedRoles = ["layer-reader"] },
                spatial: spatial)
            .AddStorageBinding(
                "bind-private-parcels",
                ResourceId,
                "private.parcels",
                storageLayerId: StorageLayerId)
            .AddService(ServiceId, ServiceName)
            .AddPublication(
                "pub-private-parcels",
                ServiceId,
                ResourceId,
                layerIndex: LayerId,
                storageBindingId: "bind-private-parcels")
            .BuildProvider();
    }

    private static ITemporaryFileService BuildTemporaryFileService()
    {
        var service = Substitute.For<ITemporaryFileService>();
        service.StoreTemporaryFileAsync(
                Arg.Any<byte[]>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<ClaimsPrincipal?>(),
                Arg.Any<CancellationToken>())
            .Returns("/temp/authz-hunt.png");
        return service;
    }
}
