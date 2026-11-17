// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.AuditLog;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Server.Tests.Features.Eval;

// An operator reviews what an agent read from the durable audit feed: one record per tool call
// and resource read, naming only the layer the agent actually queried.
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpAccessAuditPostgresIntegrationTests(DatabaseFixtureAdapter fixture) : IAsyncLifetime
{
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(McpAccessAuditPostgresIntegrationTests));
        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.layer_a (objectid bigint PRIMARY KEY, name text);
            INSERT INTO {{_schema}}.layer_a VALUES (1, 'Lot 1');
            CREATE TABLE {{_schema}}.audit_log (LIKE honua.audit_log INCLUDING ALL);
            """);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task AgentSession_ReadsAreRecordedDurably_WithTheQueriedLayerOnly()
    {
        var connectionProvider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        connectionProvider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        using var provider = Services(connectionProvider);
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "agent-reader")], "Test")),
        };
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var surface = new McpDataAccessSurface(
            [
                new ListLayersTool(jobService, NullLogger<ListLayersTool>.Instance),
                new QueryFeaturesTool(jobService, NullLogger<QueryFeaturesTool>.Instance),
            ],
            [new PromotionSurfaceIndexResource(null, null, jobService, NullLogger<PromotionSurfaceIndexResource>.Instance)],
            NullLogger<McpDataAccessSurface>.Instance);

        await DispatchAsync(surface, context, "tools/call", """{"name":"honua_list_layers","arguments":{}}""");
        await DispatchAsync(
            surface,
            context,
            "tools/call",
            """{"name":"honua_query_features","arguments":{"serviceId":"svc-audit","serviceLayerIndex":0,"outFields":["*"],"returnGeometry":false}}""");
        await DispatchAsync(surface, context, "resources/read", """{"uri":"honua://published-services"}""");

        var page = await new PostgresAuditLogReader(connectionProvider, _schema).ListAsync(
            new AuditLogFilter { ResourceType = McpAccessAudit.ResourceType, PageSize = 10 });
        var records = page.Items.Reverse().ToArray();
        records.Select(r => (r.Action, r.ResourceId, r.Outcome)).Should().Equal(
            (McpAccessAudit.ToolCallAction, "honua_list_layers", AuditOutcome.Success),
            (McpAccessAudit.ToolCallAction, "honua_query_features", AuditOutcome.Success),
            (McpAccessAudit.ResourceReadAction, "honua://published-services", AuditOutcome.Success));
        records[1].Details.Should().Be("""{"targets":{"serviceId":"svc-audit","serviceLayerIndex":0}}""");
        string.Concat(records.Select(r => r.Details)).Should().NotContain("serviceLayerIndex\":1").And.NotContain("Lot 1");
    }

    private ServiceProvider Services(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-a", "Layer A", fields:
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            ])
            .AddResource("res-b", "Layer B", fields:
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
            ])
            .AddStorageBinding("bind-a", "res-a", $"{_schema}.layer_a", storageLayerId: 1)
            .AddStorageBinding("bind-b", "res-b", $"{_schema}.layer_a", storageLayerId: 2)
            .AddService("svc-audit", "Audit")
            .AddPublication("pub-a", "svc-audit", "res-a", layerIndex: 0, storageBindingId: "bind-a")
            .AddPublication("pub-b", "svc-audit", "res-b", layerIndex: 1, storageBindingId: "bind-b")
            .Build();
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<string>.Empty);
        var reader = new PostgresStorageMappedFeatureReader(
            connectionProvider,
            new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            graph.Resources[0],
            new FeatureStorageMapping("layer_a", SchemaName: _schema, PrimaryKeyColumn: "objectid", GeometryColumn: null),
            connection: null,
            connectionEncryptionService: null,
            fieldMaskSource: masks);

        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddOptions<RbacOptions>();
        services.AddSingleton<IFeatureReader>(reader);
        services.AddSingleton(Substitute.For<IGeometryService>());
        services.AddSingleton(Substitute.For<IFilterExpressionService>());
        services.AddSingleton<IAuditLog>(
            new PostgresAuditLog(connectionProvider, NullLogger<PostgresAuditLog>.Instance, schemaName: _schema));
        return services.BuildServiceProvider();
    }

    private static async Task DispatchAsync(McpDataAccessSurface surface, HttpContext context, string method, string parameters)
    {
        using var document = JsonDocument.Parse(parameters);
        var response = await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonSerializer.SerializeToElement(method),
            Method = method,
            Params = document.RootElement.Clone(),
        }, CancellationToken.None);

        response!.Error.Should().BeNull();
        if (method == "tools/call")
        {
            var result = response.Result!.Value;
            result.GetProperty("isError").GetBoolean().Should().BeFalse(result.GetRawText());
        }
    }
}
