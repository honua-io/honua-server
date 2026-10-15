// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Discovery;
using Honua.Ai.Grounding;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Publisher-hidden fields stay out of MCP feature queries, layer descriptions, and the
/// grounding catalog (#6118).
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class QueryFeaturesToolTests
{
    private const string ServiceId = "svc-parcels";
    private const string HiddenField = "salary";
    private const string HiddenValue = "secret-salary";
    private const int StorageLayerId = 42;

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_DefaultProjection_OmitsHiddenField()
    {
        var reader = Substitute.For<IFeatureReader>();
        FeatureQuery captured = default;
        reader.QueryAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<FeatureQuery>();
                return FeatureWithHiddenValue();
            });

        var response = await DispatchAsync(
            reader,
            QueryFeaturesTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":0,"returnGeometry":false}""");

        var structured = SuccessfulContent(response);
        structured.GetProperty("features")[0].GetProperty("attributes").TryGetProperty("name", out _).Should().BeTrue();
        structured.GetProperty("features")[0].GetProperty("attributes").TryGetProperty(HiddenField, out _).Should().BeFalse();
        structured.GetProperty("geojson").GetProperty("features")[0].GetProperty("properties")
            .TryGetProperty(HiddenField, out _).Should().BeFalse();
        response.Result!.Value.GetRawText().Should().NotContain(HiddenValue);
        captured.OutFields.Should().NotBeNull();
        captured.OutFields!.Value.Should().Contain("name").And.NotContain(HiddenField).And.NotContain("shape");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_OutFieldsNamingHiddenField_ReturnsInvalidArgument()
    {
        var reader = Substitute.For<IFeatureReader>();
        var response = await DispatchAsync(
            reader,
            QueryFeaturesTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":0,"outFields":["{{HiddenField}}"]}""");

        AssertInvalidArgument(response);
        await reader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_WhereOnHiddenField_ReturnsInvalidArgument()
    {
        var reader = Substitute.For<IFeatureReader>();
        var response = await DispatchAsync(
            reader,
            QueryFeaturesTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":0,"where":"{{HiddenField}} = 'x'"}""");

        AssertInvalidArgument(response);
        await reader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_describe_layer")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task DescribeLayer_OmitsHiddenField()
    {
        var reader = Substitute.For<IFeatureReader>();
        var response = await DispatchAsync(
            reader,
            DescribeLayerTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":0,"includeRowCount":false}""");

        var structured = SuccessfulContent(response);
        var names = structured.GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString())
            .ToArray();
        names.Should().Contain("name").And.NotContain(HiddenField);
        structured.GetProperty("fieldCount").GetInt32().Should().Be(names.Length);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_describe_layer")]
    public void GroundingCatalog_OmitsHiddenField()
    {
        var graph = BuildGraph();
        var snapshot = new MetadataV2GraphSnapshot(graph, "etag", DateTimeOffset.UtcNow);
        var layers = MetadataV2GroundingCatalog.BuildLayers(
            snapshot,
            [new ReadableMetadataPublication(graph.Services[0], graph.Publications[0], graph.Resources[0])]);

        layers.Should().ContainSingle();
        layers[0].Fields.Select(field => field.Name).Should().Contain("name").And.NotContain(HiddenField);
    }

    private async Task<McpJsonRpcResponse> DispatchAsync(IFeatureReader reader, string toolName, string argumentsJson)
    {
        var surface = new McpDataAccessSurface(
            [
                new QueryFeaturesTool(_jobService, NullLogger<QueryFeaturesTool>.Instance),
                new DescribeLayerTool(_jobService, NullLogger<DescribeLayerTool>.Instance)
            ],
            [],
            NullLogger<McpDataAccessSurface>.Instance);
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = BuildServices(reader);
        using var parameters = JsonDocument.Parse($$"""{"name":"{{toolName}}","arguments":{{argumentsJson}}}""");
        return (await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonSerializer.SerializeToElement("hidden"),
            Method = "tools/call",
            Params = parameters.RootElement.Clone()
        }, CancellationToken.None))!;
    }

    private static ServiceProvider BuildServices(IFeatureReader reader)
    {
        var sql = Substitute.For<ISqlFilterTranslator>();
        sql.Translate(Arg.Any<FilterExpression>(), Arg.Any<MetadataV2Resource>())
            .Returns(new SqlFragment("TRUE", []));
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(BuildGraph()));
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddOptions<RbacOptions>();
        services.AddSingleton(reader);
        services.AddSingleton(Substitute.For<IGeometryService>());
        services.AddSingleton<IFilterExpressionService>(new FilterExpressionService(new FilterExpressionTranslator(sql)));
        return services.BuildServiceProvider();
    }

    private static MetadataV2Graph BuildGraph()
        => new TestMetadataV2GraphBuilder()
            .AddResource(
                "res-parcels",
                "Parcels",
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
                    new MetadataV2Field { Name = HiddenField, Type = MetadataV2FieldType.String, Nullable = true, Hidden = true },
                    new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true }
                ])
            .AddStorageBinding("bind-parcels", "res-parcels", "public.parcels", storageLayerId: StorageLayerId)
            .AddService(ServiceId, "Parcels")
            .AddPublication("pub-parcels", ServiceId, "res-parcels", layerIndex: 0, storageBindingId: "bind-parcels")
            .Build();

    private static QueryResult<Feature> FeatureWithHiddenValue()
        => new()
        {
            TotalCount = 1,
            Items =
            [
                new Feature
                {
                    Id = 7,
                    Attributes = ImmutableDictionary<string, object?>.Empty
                        .Add("name", "Lot 7")
                        .Add(HiddenField, HiddenValue)
                }
            ]
        };

    private static JsonElement SuccessfulContent(McpJsonRpcResponse response)
    {
        response.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse(result.GetRawText());
        return result.GetProperty("structuredContent");
    }

    private static void AssertInvalidArgument(McpJsonRpcResponse response)
    {
        response.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue(result.GetRawText());
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("invalid_argument");
        result.GetRawText().Should().NotContain(HiddenValue);
    }
}
