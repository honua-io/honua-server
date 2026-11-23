// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Configuration;
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
using Honua.Infrastructure.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Eval;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpQueryBboxPostgresIntegrationTests(DatabaseFixtureAdapter fixture) : IAsyncLifetime
{
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(McpQueryBboxPostgresIntegrationTests));
        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.zones (
                objectid bigint PRIMARY KEY,
                name text,
                geom geometry(Polygon, 4326)
            );
            INSERT INTO {{_schema}}.zones VALUES
                (27, 'Triangle zone', ST_GeomFromText('POLYGON((0 0,10 0,0 10,0 0))', 4326));
            """);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    // The triangle's envelope is [0,0,10,10]; [8,8,9,9] lies inside that envelope but
    // outside the triangle (x + y > 10), while [1,1,2,2] lies inside the triangle.
    [IntegrationTheory]
    [InlineData(new[] { 8.0, 8.0, 9.0, 9.0 }, 0)]
    [InlineData(new[] { 1.0, 1.0, 2.0, 2.0 }, 1)]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_Bbox_MatchesFeatureGeometryNotItsEnvelope(double[] bbox, int expectedCount)
    {
        using var provider = BuildServices();
        var result = await CallQueryFeaturesAsync(provider, new Dictionary<string, object> { ["bbox"] = bbox });

        result.GetProperty("isError").GetBoolean().Should().BeFalse(result.GetRawText());
        result.GetProperty("structuredContent").GetProperty("returnedCount").GetInt32()
            .Should().Be(expectedCount);
    }

    // The line from (2,9) to (9,2) runs outside the triangle (x + y = 11 > 10), but its rectangle
    // [2,2,9,9] covers (2,2), which is inside: a bbox hit that the geometry filter must not report.
    [IntegrationTheory]
    [InlineData("""{"type":"LineString","coordinates":[[2,9],[9,2]]}""", 0)]
    [InlineData("""{"type":"Polygon","coordinates":[[[1,1],[2,1],[2,2],[1,2],[1,1]]]}""", 1)]
    [InlineData("""{"type":"MultiPoint","coordinates":[[8,8],[1,1]]}""", 1)]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_Geometry_MatchesFeatureGeometryNotTheFilterRectangle(string geometry, int expectedCount)
    {
        using var provider = BuildServices();
        var result = await CallQueryFeaturesAsync(provider, new Dictionary<string, object>
        {
            ["geometry"] = JsonDocument.Parse(geometry).RootElement.Clone(),
        });

        result.GetProperty("isError").GetBoolean().Should().BeFalse(result.GetRawText());
        result.GetProperty("structuredContent").GetProperty("returnedCount").GetInt32()
            .Should().Be(expectedCount);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_LineRectangle_HitsWhereTheLineItselfMisses()
    {
        using var provider = BuildServices();

        var byRectangle = await CallQueryFeaturesAsync(provider, new Dictionary<string, object>
        {
            ["bbox"] = new[] { 2.0, 2.0, 9.0, 9.0 },
        });
        var byLine = await CallQueryFeaturesAsync(provider, new Dictionary<string, object>
        {
            ["geometry"] = JsonDocument.Parse("""{"type":"LineString","coordinates":[[2,9],[9,2]]}""").RootElement.Clone(),
        });

        byRectangle.GetProperty("structuredContent").GetProperty("returnedCount").GetInt32().Should().Be(1,
            "the rectangle around the line covers part of the triangle");
        byLine.GetProperty("structuredContent").GetProperty("returnedCount").GetInt32().Should().Be(0,
            "the line itself never enters the triangle");
    }

    [IntegrationTheory]
    [InlineData("""{"bbox":[0,0,1,1],"geometry":{"type":"Point","coordinates":[1,1]}}""", "not both")]
    [InlineData("""{"geometry":{"type":"Feature","geometry":{"type":"Point","coordinates":[1,1]}}}""", "not supported")]
    [InlineData("""{"geometry":{"type":"GeometryCollection","geometries":[]}}""", "not supported")]
    [InlineData("""{"geometry":{"type":"Point","coordinates":[1,1]},"geometrySrid":0}""", "geometrySrid")]
    [InlineData("""{"geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1]]]}}""", "refused")]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_InvalidGeometryFilter_IsAnInvalidArgument(string extraArguments, string messageFragment)
    {
        using var provider = BuildServices();
        var extra = JsonDocument.Parse(extraArguments).RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object)property.Value.Clone());

        var result = await CallQueryFeaturesAsync(provider, extra);

        AssertInvalidArgument(result, messageFragment);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task QueryFeatures_OverLimitGeometry_IsRefusedWithSimplifyGuidanceNotAnInternalError()
    {
        using var provider = BuildServices(new LimitsOptions { Geometry = new GeometryLimits { MaxVerticesPerGeometry = 1_000 } });
        var coordinates = Enumerable.Range(0, 1_001).Select(i => new[] { i * 0.001, 1.0 }).ToArray();

        var result = await CallQueryFeaturesAsync(provider, new Dictionary<string, object>
        {
            ["geometry"] = new Dictionary<string, object> { ["type"] = "LineString", ["coordinates"] = coordinates },
        });

        AssertInvalidArgument(result, "simplify it or split it");
    }

    private static void AssertInvalidArgument(JsonElement result, string messageFragment)
    {
        result.GetProperty("isError").GetBoolean().Should().BeTrue(result.GetRawText());
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument", result.GetRawText());
        structured.GetProperty("message").GetString().Should().Contain(messageFragment);
    }

    private ServiceProvider BuildServices(LimitsOptions? limits = null)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-zones", "Zones", fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                    new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry }
                ],
                spatial: new MetadataV2ObjectSpatial
                {
                    GeometryType = MetadataV2GeometryType.Polygon,
                    PrimaryGeometryField = "geom",
                    SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326" }
                })
            .AddStorageBinding("bind-zones", "res-zones", $"{_schema}.zones", storageLayerId: 13)
            .AddService("svc-zones", "Zones")
            .AddPublication("pub-zones", "svc-zones", "res-zones", layerIndex: 13, storageBindingId: "bind-zones")
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddOptions<RbacOptions>();
        services.AddSingleton<IFeatureReader>(CreateReader(graph.Resources[0]));
        services.AddSingleton<IGeometryService>(new GeometryService(Options.Create(limits ?? new LimitsOptions())));
        services.AddSingleton(Substitute.For<IFilterExpressionService>());
        return services.BuildServiceProvider();
    }

    private PostgresStorageMappedFeatureReader CreateReader(MetadataV2Object resource)
    {
        var connectionProvider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        connectionProvider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        return new PostgresStorageMappedFeatureReader(
            connectionProvider,
            new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            resource,
            new FeatureStorageMapping("zones", SchemaName: _schema, PrimaryKeyColumn: "objectid",
                GeometryColumn: "geom", StorageSrid: 4326),
            connection: null,
            connectionEncryptionService: null);
    }

    private static async Task<JsonElement> CallQueryFeaturesAsync(
        IServiceProvider provider,
        IReadOnlyDictionary<string, object> filter)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "bbox-reader")], "Test"))
        };
        var surface = new McpDataAccessSurface(
            [new QueryFeaturesTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<QueryFeaturesTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);
        var argumentBag = new Dictionary<string, object>
        {
            ["serviceId"] = "svc-zones",
            ["serviceLayerIndex"] = 13,
            ["returnGeometry"] = false
        };
        foreach (var (name, value) in filter)
        {
            argumentBag[name] = value;
        }

        var arguments = JsonSerializer.SerializeToElement(argumentBag);

        var response = await surface.DispatchAsync(context, new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonSerializer.SerializeToElement("bbox"),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(new { name = "honua_query_features", arguments })
        }, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        return response.Result!.Value;
    }
}
