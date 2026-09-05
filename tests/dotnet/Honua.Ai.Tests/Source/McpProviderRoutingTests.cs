// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
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

[Protocol(TestProtocols.Mcp)]
public sealed class McpProviderRoutingTests
{
    [Theory]
    [InlineData(QueryFeaturesTool.ToolName, false)]
    [InlineData(QueryFeaturesTool.ToolName, true)]
    [InlineData(DescribeLayerTool.ToolName, false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task BoundPublication_ReadsFromConnectionProvider(string toolName, bool countOnly)
    {
        var defaultReader = CreateReader(3);
        var unboundReader = CreateReader(5);
        var boundReader = CreateReader(17);
        var connection = new DataConnection { Id = "conn-remote", Provider = "duckdb" };
        var connections = Substitute.For<ISecureConnectionRegistry>();
        connections.GetConnectionAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider>();
        provider.ProviderName.Returns("duckdb");
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        provider.Reader.Returns(unboundReader);
        ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(Arg.Any<FeatureProviderBinding>())
            .Returns(boundReader);
        var graph = new TestMetadataV2GraphBuilder()
            .AddConnection(connection.Id, "Remote data", provider: "duckdb")
            .AddResource("res-remote", "Remote rows")
            .AddStorageBinding("bind-remote", "res-remote", "remote.rows", connectionId: connection.Id, storageLayerId: 42)
            .AddService("svc-remote", "Remote")
            .AddPublication("pub-remote", "svc-remote", "res-remote", layerIndex: 0, storageBindingId: "bind-remote")
            .BuildProvider();
        using var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(graph)
            .AddSingleton(defaultReader)
            .AddSingleton(new FeatureProviderQueryRouter(connections, new FeatureDataProviderRegistry([provider])))
            .AddSingleton(Substitute.For<IGeometryService>())
            .AddSingleton(Substitute.For<IFilterExpressionService>())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddOptions<RbacOptions>().Services
            .BuildServiceProvider();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var surface = new McpDataAccessSurface(
            [new QueryFeaturesTool(jobService, NullLogger<QueryFeaturesTool>.Instance),
             new DescribeLayerTool(jobService, NullLogger<DescribeLayerTool>.Instance)],
            [], NullLogger<McpDataAccessSurface>.Instance);
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        using var parameters = JsonDocument.Parse($$"""
            {"name":"{{toolName}}","arguments":{"serviceId":"svc-remote","layerId":0,"returnCountOnly":{{(countOnly ? "true" : "false")}}}}
            """);
        var response = await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonSerializer.SerializeToElement("routing"),
            Method = "tools/call",
            Params = parameters.RootElement.Clone()
        }, CancellationToken.None);

        response!.Error.Should().BeNull();
        response.Result!.Value.GetProperty("isError").GetBoolean().Should().BeFalse();
        var output = response.Result.Value.GetProperty("structuredContent");
        output.GetProperty(toolName == DescribeLayerTool.ToolName ? "rowCount" : "count").GetInt64().Should().Be(17);
        if (toolName == QueryFeaturesTool.ToolName && !countOnly)
        {
            output.GetProperty("features")[0].GetProperty("id").GetInt64().Should().Be(17);
            await boundReader.Received(1).QueryAsync(42, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await boundReader.Received(1).CountAsync(42, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        }

        ((IBindableFeatureDataProvider)provider).Received(1).CreateReaderForBinding(
            Arg.Is<FeatureProviderBinding>(binding => binding.Connection == connection
                && binding.StorageLayerId == 42 && binding.StorageBinding.Metadata.Id == "bind-remote"
                && binding.Publication.Metadata.Id == "pub-remote"));
        await connections.Received(1).GetConnectionAsync(connection.Id, Arg.Any<CancellationToken>());
        defaultReader.ReceivedCalls().Should().BeEmpty();
        unboundReader.ReceivedCalls().Should().BeEmpty();
    }

    private static IFeatureReader CreateReader(long marker)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.CountAsync(42, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>()).Returns(marker);
        reader.QueryAsync(42, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>()).Returns(new QueryResult<Feature>
        {
            TotalCount = marker,
            Items = [new Feature { Id = marker, Attributes = ImmutableDictionary<string, object?>.Empty }]
        });
        reader.ClearReceivedCalls();
        return reader;
    }
}
