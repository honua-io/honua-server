// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
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

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpQueryWildcardPostgresIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(McpQueryWildcardPostgresIntegrationTests));
        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.parcels (
                objectid bigint PRIMARY KEY,
                name text,
                secret text,
                salary text
            );
            INSERT INTO {{_schema}}.parcels VALUES (7, 'Lot 7', 'masked-value', 'hidden-value');
            """);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTest]
    [Trait("Category", "RemoteSourceExecutionProof")]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_Wildcard_RealMappedReaderReturnsVisibleUnmaskedAttributes()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-parcels", "Parcels", fields:
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "secret", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "salary", Type = MetadataV2FieldType.String, Hidden = true }
            ])
            .AddStorageBinding("bind-parcels", "res-parcels", $"{_schema}.parcels", storageLayerId: 42)
            .AddService("svc-parcels", "Parcels")
            .AddPublication("pub-parcels", "svc-parcels", "res-parcels", layerIndex: 0,
                storageBindingId: "bind-parcels")
            .Build();
        var connectionProvider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        connectionProvider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("secret"));
        var reader = new PostgresStorageMappedFeatureReader(
            connectionProvider,
            new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            graph.Resources[0],
            new FeatureStorageMapping("parcels", SchemaName: _schema, PrimaryKeyColumn: "objectid",
                GeometryColumn: null),
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
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "wildcard-reader")], "Test"))
        };
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var surface = new McpDataAccessSurface(
            [new QueryFeaturesTool(jobService, NullLogger<QueryFeaturesTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);
        using var parameters = JsonDocument.Parse("""
            {"name":"honua_query_features","arguments":{
              "serviceId":"svc-parcels","layerId":0,"outFields":["*"],"returnGeometry":false
            }}
            """);

        var response = await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonSerializer.SerializeToElement("wildcard"),
            Method = "tools/call",
            Params = parameters.RootElement.Clone()
        }, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse(result.GetRawText());
        var output = result.GetProperty("structuredContent");
        output.GetProperty("returnedCount").GetInt32().Should().Be(1);
        var attributes = output.GetProperty("features")[0].GetProperty("attributes");
        attributes.GetProperty("name").GetString().Should().Be("Lot 7");
        attributes.TryGetProperty("secret", out _).Should().BeFalse();
        attributes.TryGetProperty("salary", out _).Should().BeFalse();
        var properties = output.GetProperty("geojson").GetProperty("features")[0].GetProperty("properties");
        properties.GetProperty("name").GetString().Should().Be("Lot 7");
        properties.TryGetProperty("secret", out _).Should().BeFalse();
        properties.TryGetProperty("salary", out _).Should().BeFalse();
        result.GetRawText().Should().NotContain("masked-value").And.NotContain("hidden-value");
    }
}
