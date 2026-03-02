// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Sdk.Admin.Models;
using Honua.Sdk.Admin.Tests.Fixtures;

namespace Honua.Sdk.Admin.Tests;

public sealed class LayerPublishingTests
{
    private const string ConnectionId = "11111111-1111-1111-1111-111111111111";

    private static object CreateLayerSummary(int layerId = 0) => new
    {
        layerId,
        layerName = "test_layer",
        schema = "public",
        table = "test_table",
        geometryType = "Point",
        srid = 4326,
        primaryKey = "gid",
        fieldCount = 5,
        enabled = true,
        serviceName = "default"
    };

    [Fact]
    public async Task ListLayersAsync_ReturnsLayers()
    {
        var layers = new[] { CreateLayerSummary(0), CreateLayerSummary(1) };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains($"/admin/connections/{ConnectionId}/layers/", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(layers));
        });

        var result = await client.ListLayersAsync(ConnectionId);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ListLayersAsync_WithServiceName_PassesQueryParam()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("serviceName=myservice", req.RequestUri!.Query);
            return Task.FromResult(TestHelpers.CreateJsonResponse(Array.Empty<object>()));
        });

        await client.ListLayersAsync(ConnectionId, serviceName: "myservice");
    }

    [Fact]
    public async Task PublishLayerAsync_SendsPostAndReturnsSummary()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains($"/admin/connections/{ConnectionId}/layers/", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(CreateLayerSummary(42), HttpStatusCode.Created));
        });

        var result = await client.PublishLayerAsync(ConnectionId, new PublishLayerRequest
        {
            Schema = "public",
            Table = "test_table",
            LayerName = "test_layer"
        });

        Assert.Equal(42, result.LayerId);
        Assert.Equal("test_layer", result.LayerName);
    }

    [Fact]
    public async Task SetLayerEnabledAsync_SendsPutAndReturnsSummary()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains($"/admin/connections/{ConnectionId}/layers/5/enabled", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(CreateLayerSummary(5)));
        });

        var result = await client.SetLayerEnabledAsync(ConnectionId, 5, true);

        Assert.Equal(5, result.LayerId);
    }

    [Fact]
    public async Task SetServiceLayersEnabledAsync_ReturnsAllLayers()
    {
        var layers = new[] { CreateLayerSummary(0), CreateLayerSummary(1) };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains($"/admin/connections/{ConnectionId}/layers/enabled", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(layers));
        });

        var result = await client.SetServiceLayersEnabledAsync(ConnectionId, false);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task DiscoverTablesAsync_ReturnsDiscoveredTables()
    {
        var discoveryResponse = new
        {
            tables = new[]
            {
                new
                {
                    schema = "public",
                    table = "cities",
                    geometryColumn = "geom",
                    geometryType = "POINT",
                    srid = 4326,
                    estimatedRows = (long?)5000,
                    columns = new[]
                    {
                        new { name = "gid", dataType = "integer", isNullable = false, isPrimaryKey = true, maxLength = (int?)null },
                        new { name = "name", dataType = "varchar", isNullable = true, isPrimaryKey = false, maxLength = (int?)255 }
                    }
                }
            }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains($"/admin/connections/{ConnectionId}/tables", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateRawJsonResponse(discoveryResponse));
        });

        var result = await client.DiscoverTablesAsync(ConnectionId);

        Assert.Single(result.Tables);
        Assert.Equal("cities", result.Tables[0].Table);
        Assert.Equal(2, result.Tables[0].Columns.Count);
    }

    [Fact]
    public async Task GetLayerStyleAsync_ReturnsStyle()
    {
        var style = new
        {
            mapLibreStyle = JsonDocument.Parse("{\"layers\":[]}").RootElement,
            drawingInfo = (JsonElement?)null
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/metadata/layers/7/style", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(style));
        });

        var result = await client.GetLayerStyleAsync(7);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task UpdateLayerStyleAsync_SendsPutAndReturnsStyle()
    {
        var style = new
        {
            mapLibreStyle = JsonDocument.Parse("{\"layers\":[{\"id\":\"fill\"}]}").RootElement,
            drawingInfo = (JsonElement?)null
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/admin/metadata/layers/7/style", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(style));
        });

        var result = await client.UpdateLayerStyleAsync(7, new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonDocument.Parse("{\"layers\":[{\"id\":\"fill\"}]}").RootElement
        });

        Assert.NotNull(result);
    }
}
