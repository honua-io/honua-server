// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Databricks.Features.FeatureStore.Services;
using Honua.Databricks.Features.Infrastructure;
using Microsoft.Extensions.Options;

namespace Honua.Databricks.Tests;

public class DatabricksStatementFlowTests
{
    private static readonly DatabricksLayerMapping Mapping = new()
    {
        LayerId = 1,
        Table = "parcels",
        Catalog = "main",
        Schema = "gis",
        GeometryColumn = "geom",
        PrimaryKeyColumn = "id",
        Srid = 4326,
        GeometryType = GeometryType.Point,
        AttributeColumns = ["name"],
    };

    private static (DatabricksStatementClient Client, StubHttpMessageHandler Handler) CreateClient(StubHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.cloud.databricks.com") };
        var options = Options.Create(new DatabricksOptions
        {
            Host = "https://example.cloud.databricks.com",
            WarehouseId = "wh123",
            Token = "secret-token",
            CommandTimeoutSeconds = 30,
            PollIntervalMilliseconds = 1,
        });
        return (new DatabricksStatementClient(httpClient, options), handler);
    }

    [Fact]
    public async Task ExecuteAsync_ImmediateSuccess_ParsesColumnsAndRows()
    {
        const string json = """
        {
          "statement_id": "abc",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [
            { "name": "__honua_id", "type_name": "LONG", "position": 0 },
            { "name": "__honua_geom_hex", "type_name": "STRING", "position": 1 }
          ] } },
          "result": { "data_array": [ ["1", "0101000000000000000000F03F0000000000000040"] ] }
        }
        """;

        var (client, handler) = CreateClient(new StubHttpMessageHandler().EnqueueJson(json));
        var result = await client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None);

        Assert.Equal(["__honua_id", "__honua_geom_hex"], result.Columns);
        Assert.Single(result.Rows);
        // One submit request; no polling because the statement succeeded immediately.
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ExecuteAsync_PendingThenSucceeded_PollsStatus()
    {
        const string pending = """{ "statement_id": "abc", "status": { "state": "PENDING" } }""";
        const string succeeded = """
        {
          "statement_id": "abc",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [ { "name": "c", "position": 0 } ] } },
          "result": { "data_array": [ ["42"] ] }
        }
        """;

        var handler = new StubHttpMessageHandler().EnqueueJson(pending).EnqueueJson(succeeded);
        var (client, recorded) = CreateClient(handler);

        var result = await client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Equal(2, recorded.Requests.Count);
        Assert.Equal(HttpMethod.Get, recorded.Requests[1].Method);
        Assert.Contains("abc", recorded.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleChunks_FollowsNextChunkLink()
    {
        const string firstChunk = """
        {
          "statement_id": "abc",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [ { "name": "c", "position": 0 } ] } },
          "result": { "data_array": [ ["1"] ], "next_chunk_internal_link": "/api/2.0/sql/statements/abc/result/chunks/1" }
        }
        """;
        const string secondChunk = """{ "result": { "data_array": [ ["2"], ["3"] ] } }""";

        var handler = new StubHttpMessageHandler().EnqueueJson(firstChunk).EnqueueJson(secondChunk);
        var (client, recorded) = CreateClient(handler);

        var result = await client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(2, recorded.Requests.Count);
    }

    [Fact]
    public async Task ExecuteAsync_FailedStatement_ThrowsWithoutLeakingToken()
    {
        const string failed = """
        { "statement_id": "abc", "status": { "state": "FAILED", "error": { "error_code": "BAD_REQUEST", "message": "boom" } } }
        """;

        var (client, _) = CreateClient(new StubHttpMessageHandler().EnqueueJson(failed));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None));

        Assert.Contains("BAD_REQUEST", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataAccess_ExecuteSelect_DecodesWkbHexAndAttributes()
    {
        const string json = """
        {
          "statement_id": "abc",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [
            { "name": "__honua_id", "position": 0 },
            { "name": "__honua_geom_hex", "position": 1 },
            { "name": "name", "position": 2 }
          ] } },
          "result": { "data_array": [ ["7", "0102", "acme"] ] }
        }
        """;

        var (client, _) = CreateClient(new StubHttpMessageHandler().EnqueueJson(json));
        var dataAccess = new DatabricksFeatureDataAccess(client);

        var features = await dataAccess.ExecuteSelectAsync(
            Mapping, DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None);

        var feature = Assert.Single(features);
        Assert.Equal(7, feature.Id);
        Assert.Equal(new byte[] { 0x01, 0x02 }, feature.Geometry);
        Assert.Equal("acme", feature.Attributes["name"]);
    }

    [Fact]
    public async Task DataAccess_ExecuteCount_ReturnsScalar()
    {
        const string json = """
        {
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [ { "name": "__honua_count", "position": 0 } ] } },
          "result": { "data_array": [ ["123"] ] }
        }
        """;

        var (client, _) = CreateClient(new StubHttpMessageHandler().EnqueueJson(json));
        var dataAccess = new DatabricksFeatureDataAccess(client);

        var count = await dataAccess.ExecuteCountAsync(DatabricksSqlStatement.WithoutParameters("SELECT COUNT(*)"), CancellationToken.None);

        Assert.Equal(123, count);
    }

    [Fact]
    public async Task DataAccess_ExecuteExtent_ReturnsBoundingBox()
    {
        const string json = """
        {
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [
            { "name": "__honua_minx", "position": 0 },
            { "name": "__honua_miny", "position": 1 },
            { "name": "__honua_maxx", "position": 2 },
            { "name": "__honua_maxy", "position": 3 }
          ] } },
          "result": { "data_array": [ ["-10.5", "-5.25", "10.5", "5.25"] ] }
        }
        """;

        var (client, _) = CreateClient(new StubHttpMessageHandler().EnqueueJson(json));
        var dataAccess = new DatabricksFeatureDataAccess(client);

        var extent = await dataAccess.ExecuteExtentAsync(
            Mapping, DatabricksSqlStatement.WithoutParameters("SELECT extent"), CancellationToken.None);

        Assert.NotNull(extent);
        Assert.Equal(-10.5, extent!.Value.MinX);
        Assert.Equal(5.25, extent.Value.MaxY);
        Assert.Equal(4326, extent.Value.SpatialReference);
    }
}
