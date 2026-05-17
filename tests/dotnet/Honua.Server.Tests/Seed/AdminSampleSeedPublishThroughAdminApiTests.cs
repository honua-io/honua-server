// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Seeding;

namespace Honua.Server.Tests.Seed;

/// <summary>
/// Verifies the admin sample seed can be registered through the layer publishing API.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Protocol(TestProtocols.FeatureServer)]
public sealed class AdminSampleSeedPublishThroughAdminApiTests : IAsyncLifetime
{
    private const double CoordinateTolerance = 0.0002;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;
    private string _schema = string.Empty;
    private string _serviceName = string.Empty;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "server.yaml"));
        await _fixture.InitializeAsync();

        _client = _fixture.CreateAdminClient();
        _schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        _serviceName = $"admin_sample_publish_{Guid.NewGuid().ToString("N")[..12]}";

        await _fixture.Postgres.ApplySeedAsync(
            ResolveRepoFile("tests", "seed", "admin-sample-feature-server.yaml"),
            _schema);
    }

    public async Task DisposeAsync()
    {
        await CleanupPublishedServiceAsync();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnCountOnly=true")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnExtentOnly=true")]
    public async Task AdminSampleSourceTables_PublishThroughAdminApi_ReturnFeatureServerCountsAndExtents()
    {
        var connectionId = await _fixture.GetTestSecureConnectionIdAsync();
        connectionId.Should().NotBeNull("the fixture should provision the test secure connection");

        var samples = new[]
        {
            new SampleLayer(
                "admin_sample_sites_source",
                "Published Oahu Operations Sites",
                "Point",
                4,
                -158.0011,
                21.3069,
                -157.7394,
                21.3972),
            new SampleLayer(
                "admin_sample_routes_source",
                "Published Oahu Response Routes",
                "LineString",
                3,
                -158.0011,
                21.3069,
                -157.7394,
                21.3972),
            new SampleLayer(
                "admin_sample_areas_source",
                "Published Oahu Service Areas",
                "Polygon",
                2,
                -157.95,
                21.29,
                -157.70,
                21.43)
        };

        foreach (var sample in samples)
        {
            var published = await PublishLayerAsync(connectionId!.Value, sample);

            published.Schema.Should().Be(_schema);
            published.Table.Should().Be(sample.Table);
            published.ServiceName.Should().Be(_serviceName);
            published.GeometryType.Should().Be(sample.GeometryType);

            await AssertCountAsync(published.LayerId, sample.ExpectedCount);
            await AssertExtentAsync(
                published.LayerId,
                sample.ExpectedXmin,
                sample.ExpectedYmin,
                sample.ExpectedXmax,
                sample.ExpectedYmax);
        }
    }

    private async Task<PublishedLayerSummary> PublishLayerAsync(Guid connectionId, SampleLayer sample)
    {
        var request = new PublishLayerRequest
        {
            Schema = _schema,
            Table = sample.Table,
            LayerName = sample.LayerName,
            Description = "Admin sample seed source table publish smoke test",
            GeometryColumn = "geometry",
            GeometryType = sample.GeometryType,
            Srid = 4326,
            PrimaryKey = "objectid",
            Fields = ["objectid", "name", "category", "status", "priority", "owner", "updated_at"],
            ServiceName = _serviceName,
            Enabled = true
        };

        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/connections/{connectionId}/layers",
            request,
            _jsonOptions);
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {payload}");
        var api = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(payload, _jsonOptions);

        api.Should().NotBeNull();
        api!.Success.Should().BeTrue();
        api.Data.Should().NotBeNull();
        return api.Data!;
    }

    private async Task AssertCountAsync(int layerId, int expectedCount)
    {
        using var response = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{layerId}/query?where=1%3D1&returnCountOnly=true&f=json");
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");
        using var document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(expectedCount);
    }

    private async Task AssertExtentAsync(
        int layerId,
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax)
    {
        using var response = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{layerId}/query?where=1%3D1&returnExtentOnly=true&outSR=4326&f=json");
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");
        using var document = JsonDocument.Parse(payload);
        var extent = document.RootElement.GetProperty("extent");

        extent.ValueKind.Should().Be(JsonValueKind.Object);
        extent.GetProperty("xmin").GetDouble().Should().BeApproximately(expectedXmin, CoordinateTolerance);
        extent.GetProperty("ymin").GetDouble().Should().BeApproximately(expectedYmin, CoordinateTolerance);
        extent.GetProperty("xmax").GetDouble().Should().BeApproximately(expectedXmax, CoordinateTolerance);
        extent.GetProperty("ymax").GetDouble().Should().BeApproximately(expectedYmax, CoordinateTolerance);
        extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    private async Task CleanupPublishedServiceAsync()
    {
        if (string.IsNullOrWhiteSpace(_serviceName))
        {
            return;
        }

        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS admin_sample_publish_cleanup_layers;

            CREATE TEMP TABLE admin_sample_publish_cleanup_layers (
                layer_id integer PRIMARY KEY
            );

            INSERT INTO admin_sample_publish_cleanup_layers (layer_id)
            SELECT layer_id
            FROM honua.service_layers
            WHERE service_name = @serviceName
            ON CONFLICT DO NOTHING;

            DELETE FROM features
            WHERE layer_id IN (SELECT layer_id FROM admin_sample_publish_cleanup_layers);

            DELETE FROM honua.layer_fields
            WHERE layer_id IN (SELECT layer_id FROM admin_sample_publish_cleanup_layers);

            DELETE FROM honua.service_layers
            WHERE service_name = @serviceName;

            DELETE FROM honua.layers
            WHERE layer_id IN (SELECT layer_id FROM admin_sample_publish_cleanup_layers);

            DELETE FROM honua.services
            WHERE service_name = @serviceName;

            DROP TABLE admin_sample_publish_cleanup_layers;
            """;
        command.Parameters.AddWithValue("serviceName", _serviceName);

        await command.ExecuteNonQueryAsync();
    }

    private static string ResolveRepoFile(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test should run under the repository output tree");
        return Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray());
    }

    private sealed record SampleLayer(
        string Table,
        string LayerName,
        string GeometryType,
        int ExpectedCount,
        double ExpectedXmin,
        double ExpectedYmin,
        double ExpectedXmax,
        double ExpectedYmax);
}
