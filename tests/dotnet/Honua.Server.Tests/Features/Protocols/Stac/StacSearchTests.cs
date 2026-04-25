// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.ServiceDefaults;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Integration tests for the STAC Search endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacSearchTests : IAsyncLifetime
{
    private static readonly double[] OutOfRangeBbox = [200.0, 95.0, 210.0, 100.0];

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_RecordsSharedTelemetryForSearchWork()
    {
        var stoppedActivities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HonuaTelemetry.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("honua.stac.", StringComparison.Ordinal))
                {
                    stoppedActivities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var response = await _fixture.Client.GetAsync("/stac/search?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchWork = stoppedActivities
            .Should()
            .ContainSingle(activity => activity.OperationName == "honua.stac.search.work")
            .Subject;
        searchWork.GetTagItem(HonuaTelemetry.Tags.Protocol).Should().Be("STAC");
        searchWork.GetTagItem(HonuaTelemetry.Tags.Operation).Should().Be("search.work");
        searchWork.GetTagItem("honua.stac.search.limit").Should().Be(1);
        searchWork.GetTagItem("honua.stac.returned").Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WhenFeatureReaderFails_RecordsSearchWorkException()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<QueryResult<Feature>>(new InvalidOperationException("reader failure")));

        var fixture = new WebAppFixture()
            .ReplaceService<IFeatureReader>(reader);

        var stoppedActivities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HonuaTelemetry.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("honua.stac.", StringComparison.Ordinal))
                {
                    stoppedActivities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.Client.GetAsync("/stac/search?limit=1");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

            var searchWork = stoppedActivities
                .Should()
                .ContainSingle(activity => activity.OperationName == "honua.stac.search.work")
                .Subject;
            searchWork.Status.Should().Be(ActivityStatusCode.Error);
            searchWork.GetTagItem(HonuaTelemetry.Tags.Error).Should().Be(true);
            searchWork.Events.Should().Contain(e => e.Name == "exception");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_NoFilters_ReturnsItems()
    {
        var response = await _fixture.Client.GetAsync("/stac/search");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").EnumerateArray().Should().NotBeEmpty();
        json.RootElement.GetProperty("context").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithLimit_RespectsLimit()
    {
        var response = await _fixture.Client.GetAsync("/stac/search?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Length.Should().BeLessThanOrEqualTo(2);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithCollections_FiltersResults()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        foreach (var item in json.RootElement.GetProperty("features").EnumerateArray())
        {
            item.GetProperty("collection").GetString().Should().Be(collectionId);
        }
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithStringIds_FiltersByStacItemId()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var itemId = $"stac-search-get-{Guid.NewGuid():N}";
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "STAC Search GET String ID");
        await SetFeatureAttributeAsync(featureId, "id", itemId);

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={Uri.EscapeDataString(itemId)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("features").EnumerateArray()
            .Should().ContainSingle(feature => feature.GetProperty("id").GetString() == itemId);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithStringIds_FiltersByStacItemId()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var itemId = $"stac-search-post-{Guid.NewGuid():N}";
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "STAC Search POST String ID");
        await SetFeatureAttributeAsync(featureId, "stac_id", itemId);
        var body = JsonSerializer.Serialize(new
        {
            collections = new[] { collectionId },
            ids = new[] { itemId }
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("features").EnumerateArray()
            .Should().ContainSingle(feature => feature.GetProperty("id").GetString() == itemId);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithUnsupportedExplicitGeometryCrsInFilter_ReturnsBadRequest()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var filterJson =
            """{"op":"s_intersects","args":[{"property":"geometry"},{"type":"Point","coordinates":[0,0],"crs":{"type":"name","properties":{"name":"EPSG:999999"}}}]}""";
        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&filter-lang=cql2-json&filter={Uri.EscapeDataString(filterJson)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);
        problem.GetProperty("detail").GetString().Should().Contain("Unsupported explicit geometry CRS 'EPSG:999999'");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithRegistryBackedFilterCrs_AcceptsNonDefaultCrs()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var projectedPoint = await TransformSeedPointAsync(3395);
        var filter = $"S_INTERSECTS(geometry, {BuildSquarePolygonText(projectedPoint)})";
        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&filter={Uri.EscapeDataString(filter)}&filter-crs=EPSG:3395");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("features").EnumerateArray()
            .Should().Contain(feature => feature.GetProperty("id").GetString() == "1", content);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithRegistryBackedExplicitGeometryCrsInFilter_AcceptsNonDefaultCrs()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var projectedPoint = await TransformSeedPointAsync(3395);
        var ring = BuildSquareRing(projectedPoint);
        var filterJson = JsonSerializer.Serialize(new
        {
            op = "s_intersects",
            args = new object[]
            {
                new { property = "geometry" },
                new
                {
                    type = "Polygon",
                    coordinates = new[] { ring },
                    crs = new
                    {
                        type = "name",
                        properties = new { name = "EPSG:3395" }
                    }
                }
            }
        });
        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&filter-lang=cql2-json&filter={Uri.EscapeDataString(filterJson)}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("features").EnumerateArray()
            .Should().Contain(feature => feature.GetProperty("id").GetString() == "1", content);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithBody_ReturnsItems()
    {
        var body = JsonSerializer.Serialize(new
        {
            limit = 5,
            collections = new[] { WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").EnumerateArray().Should().NotBeEmpty();
        json.RootElement.GetProperty("context").GetProperty("limit")
            .GetInt32().Should().Be(5);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithInvalidSortDirection_ReturnsBadRequest()
    {
        var body = JsonSerializer.Serialize(new
        {
            collections = new[] { WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture) },
            sortby = new[] { new { field = "name", direction = "sideways" } }
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid sort direction");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithBbox_ReturnsFilteredResults()
    {
        var body = JsonSerializer.Serialize(new
        {
            bbox = new[] { -180.0, -90.0, 180.0, 90.0 },
            limit = 5
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithThreeDimensionalBbox_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/stac/search?bbox=170,-10,-170,10,5,6");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithOutOfRangeBbox_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/stac/search?bbox=200,95,210,100");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithThreeDimensionalBbox_ReturnsBadRequest()
    {
        var body = JsonSerializer.Serialize(new
        {
            bbox = new[] { 170.0, -10.0, -170.0, 10.0, 5.0, 6.0 }
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_WithOutOfRangeBbox_ReturnsBadRequest()
    {
        var body = JsonSerializer.Serialize(new
        {
            bbox = OutOfRangeBbox
        });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("POST /stac/search")]
    public async Task SearchPost_AllItemsHaveStacFields()
    {
        var body = JsonSerializer.Serialize(new { limit = 3 });

        var response = await _fixture.Client.PostAsync(
            "/stac/search",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        foreach (var item in json.RootElement.GetProperty("features").EnumerateArray())
        {
            item.GetProperty("type").GetString().Should().Be("Feature");
            item.GetProperty("stac_version").GetString().Should().Be("1.0.0");
            item.GetProperty("properties").TryGetProperty("datetime", out _).Should().BeTrue();
            item.GetProperty("links").EnumerateArray().Should().NotBeEmpty();
        }
    }

    private static double[][] BuildSquareRing((double X, double Y) point, double halfSize = 1d)
        =>
        [
            [point.X - halfSize, point.Y - halfSize],
            [point.X + halfSize, point.Y - halfSize],
            [point.X + halfSize, point.Y + halfSize],
            [point.X - halfSize, point.Y + halfSize],
            [point.X - halfSize, point.Y - halfSize]
        ];

    private static string BuildSquarePolygonText((double X, double Y) point, double halfSize = 1d)
    {
        var ring = BuildSquareRing(point, halfSize);
        return FormattableString.Invariant(
            $"POLYGON(({ring[0][0]} {ring[0][1]}, {ring[1][0]} {ring[1][1]}, {ring[2][0]} {ring[2][1]}, {ring[3][0]} {ring[3][1]}, {ring[4][0]} {ring[4][1]}))");
    }

    private async Task<(double X, double Y)> TransformSeedPointAsync(int targetSrid)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                ST_X(ST_Transform(geometry, @targetSrid)),
                ST_Y(ST_Transform(geometry, @targetSrid))
            FROM features
            WHERE layer_id = @layerId AND objectid = @objectId;
            """;
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("objectId", 1L);
        command.Parameters.AddWithValue("targetSrid", targetSrid);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetDouble(0), reader.GetDouble(1));
    }

    private async Task SetFeatureAttributeAsync(long featureId, string attributeName, string attributeValue)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE features
            SET attributes = attributes || jsonb_build_object(@attributeName, @attributeValue)
            WHERE objectid = @featureId;
            """;
        command.Parameters.AddWithValue("attributeName", attributeName);
        command.Parameters.AddWithValue("attributeValue", attributeValue);
        command.Parameters.AddWithValue("featureId", featureId);
        await command.ExecuteNonQueryAsync();
    }
}
