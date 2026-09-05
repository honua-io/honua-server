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
using Honua.Core.Features.MultiTenancy.Abstractions;
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
    [InlineData(QueryFeaturesTool.ToolName, false, "bind-remote", true, null, null)]
    [InlineData(QueryFeaturesTool.ToolName, false, null, true, null, null)]
    [InlineData(QueryFeaturesTool.ToolName, true, "bind-remote", true, null, null)]
    [InlineData(QueryFeaturesTool.ToolName, true, null, true, null, null)]
    [InlineData(DescribeLayerTool.ToolName, false, "bind-remote", true, null, null)]
    [InlineData(DescribeLayerTool.ToolName, false, null, true, null, null)]
    [InlineData(QueryFeaturesTool.ToolName, false, "bind-remote", false, null, null)]
    [InlineData(QueryFeaturesTool.ToolName, true, "bind-remote", false, null, null)]
    [InlineData(DescribeLayerTool.ToolName, false, "bind-remote", false, null, null)]
    [InlineData(QueryFeaturesTool.ToolName, false, "bind-remote", true, "tenant-a", "tenant-b")]
    [InlineData(QueryFeaturesTool.ToolName, false, "bind-remote", true, "tenant-a", null)]
    [InlineData(QueryFeaturesTool.ToolName, false, "bind-remote", true, "tenant-a", "tenant-a")]
    [InlineData(QueryFeaturesTool.ToolName, true, "bind-remote", true, "tenant-a", "tenant-b")]
    [InlineData(QueryFeaturesTool.ToolName, true, "bind-remote", true, "tenant-a", null)]
    [InlineData(QueryFeaturesTool.ToolName, true, "bind-remote", true, "tenant-a", "tenant-a")]
    [InlineData(DescribeLayerTool.ToolName, false, "bind-remote", true, "tenant-a", "tenant-b")]
    [InlineData(DescribeLayerTool.ToolName, false, "bind-remote", true, "tenant-a", null)]
    [InlineData(DescribeLayerTool.ToolName, false, "bind-remote", true, "tenant-a", "tenant-a")]
    [InlineData(ListLayersTool.ToolName, false, "bind-remote", true, "tenant-a", "tenant-b")]
    [InlineData(ListLayersTool.ToolName, false, "bind-remote", true, "tenant-a", null)]
    [InlineData(ListLayersTool.ToolName, false, "bind-remote", true, "tenant-a", "tenant-a")]
    [InlineData(ListLayersTool.ToolName, false, "bind-remote", true, null, null)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task BoundPublication_RespectsRoutingAndTenantVisibility(string toolName, bool countOnly, string? publicationBindingId, bool includeRouter, string? publicationTenant, string? requestTenant)
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
        var metadata = new TestMetadataV2GraphBuilder()
            .AddConnection(connection.Id, "Remote data", provider: "duckdb")
            .AddResource("res-remote", "Remote rows")
            .AddStorageBinding("bind-remote", "res-remote", "remote.rows", connectionId: connection.Id, storageLayerId: 42)
            .AddService("svc-remote", "Remote")
            .AddPublication("pub-remote", "svc-remote", "res-remote", layerIndex: 0, storageBindingId: publicationBindingId)
            .Build();
        var publication = metadata.Publications[0];
        var graph = new TestMetadataV2GraphProvider(metadata with
        {
            Publications = [publication with { Metadata = publication.Metadata with { Tenant = publicationTenant } }]
        });
        var geometry = Substitute.For<IGeometryService>();
        geometry.ConvertWkbToGeoJson(Arg.Any<byte[]?>()).Returns((string?)null);
        var registrations = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(graph)
            .AddSingleton(defaultReader)
            .AddSingleton(geometry)
            .AddSingleton(Substitute.For<IFilterExpressionService>())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddOptions<RbacOptions>().Services;
        if (includeRouter)
        {
            registrations.AddSingleton(new FeatureProviderQueryRouter(connections, new FeatureDataProviderRegistry([provider])));
        }
        if (requestTenant is not null)
        {
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns(requestTenant);
            registrations.AddSingleton(tenant);
        }
        using var services = registrations.BuildServiceProvider();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var surface = new McpDataAccessSurface(
            [new QueryFeaturesTool(jobService, NullLogger<QueryFeaturesTool>.Instance),
             new DescribeLayerTool(jobService, NullLogger<DescribeLayerTool>.Instance),
             new ListLayersTool(jobService, NullLogger<ListLayersTool>.Instance)],
            [], NullLogger<McpDataAccessSurface>.Instance);
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        using var parameters = JsonDocument.Parse($$"""
            {"name":"{{toolName}}","arguments":{
              "serviceId":"svc-remote","layerId":0,"returnCountOnly":{{(countOnly ? "true" : "false")}}
            }
            }
            """);
        var response = await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonSerializer.SerializeToElement("routing"),
            Method = "tools/call",
            Params = parameters.RootElement.Clone()
        }, CancellationToken.None);

        response!.Error.Should().BeNull();
        var tenantVisible = publicationTenant is null || publicationTenant == requestTenant;
        if (toolName == ListLayersTool.ToolName || !tenantVisible)
        {
            var result = response.Result!.Value;
            if (toolName == ListLayersTool.ToolName)
            {
                result.GetProperty("isError").GetBoolean().Should().BeFalse();
                var listed = result.GetProperty("structuredContent");
                listed.GetProperty("totalCount").GetInt32().Should().Be(tenantVisible ? 1 : 0);
                listed.GetProperty("layers").GetArrayLength().Should().Be(tenantVisible ? 1 : 0);
            }
            else
            {
                result.GetProperty("isError").GetBoolean().Should().BeTrue("foreign or unresolved tenants cannot read a scoped publication");
                result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("not_found");
            }
            connections.ReceivedCalls().Should().BeEmpty("tenant visibility is checked before resolving a connection");
            defaultReader.ReceivedCalls().Should().BeEmpty();
            unboundReader.ReceivedCalls().Should().BeEmpty();
            boundReader.ReceivedCalls().Should().BeEmpty();
            return;
        }
        if (!includeRouter)
        {
            var result = response.Result!.Value;
            if (toolName == DescribeLayerTool.ToolName)
            {
                result.GetProperty("isError").GetBoolean().Should().BeFalse();
                result.GetProperty("structuredContent").TryGetProperty("rowCount", out _)
                    .Should().BeFalse("unavailable row counts are omitted, never read from managed storage");
            }
            else
            {
                result.GetProperty("isError").GetBoolean().Should().BeTrue();
                result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("unavailable");
            }
            defaultReader.ReceivedCalls().Should().BeEmpty();
            unboundReader.ReceivedCalls().Should().BeEmpty();
            boundReader.ReceivedCalls().Should().BeEmpty();
            return;
        }
        response.Result!.Value.GetProperty("isError").GetBoolean().Should().BeFalse(response.Result.Value.ToString());
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
            Items = [new Feature { Id = marker, Geometry = null, Attributes = ImmutableDictionary<string, object?>.Empty }]
        });
        reader.ClearReceivedCalls();
        return reader;
    }
}
