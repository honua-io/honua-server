// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Integration tests for layer publishing admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class LayerPublishingIntegrationTests : IAsyncLifetime
{
    private static readonly string[] _idNameFields = ["id", "name"];
    private static readonly string[] _idNamePopulationFields = ["id", "name", "population"];
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private const string PublishSchema = "public";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;
    private string _schema = string.Empty;
    private Guid _connectionId;
    private string _connectionName = string.Empty;
    private bool _connectionCreated;
    private string _tableName = string.Empty;
    private string _serviceName = string.Empty;
    private int? _layerId;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
        _schema = PublishSchema;
        _tableName = $"layer_{Guid.NewGuid():N}";
        _serviceName = $"svc_{Guid.NewGuid():N}";

        await CreateSecureConnectionAsync(_fixture.Postgres.ConnectionString);
        await CreatePostGisTableAsync();
    }

    public async Task DisposeAsync()
    {
        await CleanupPublishedLayerAsync();
        await DeleteSecureConnectionAsync();
        await DropPostGisTableAsync();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/{layerId}/enabled")]
    public async Task PublishLayer_ListAndToggle_ReturnsExpectedResults()
    {
        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer publish integration test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var publishResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        var publishPayload = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {publishPayload}");
        var publishApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(publishPayload, _jsonOptions);

        publishApi.Should().NotBeNull();
        publishApi!.Success.Should().BeTrue();
        publishApi.Data.Should().NotBeNull();

        var publishedLayer = publishApi.Data!;
        _layerId = publishedLayer.LayerId;

        publishedLayer.Schema.Should().Be(_schema);
        publishedLayer.Table.Should().Be(_tableName);
        publishedLayer.LayerName.Should().Be(publishRequest.LayerName);
        publishedLayer.PrimaryKey.Should().Be("id");
        publishedLayer.FieldCount.Should().Be(4);
        publishedLayer.Enabled.Should().BeTrue();
        publishedLayer.ServiceName.Should().Be(_serviceName);

        var listResponse = await _client.GetAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers?serviceName={_serviceName}");

        listResponse.Be200Ok();

        var listPayload = await listResponse.Content.ReadAsStringAsync();
        var listApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary[]>>(listPayload, _jsonOptions);

        listApi.Should().NotBeNull();
        listApi!.Success.Should().BeTrue();
        listApi.Data.Should().NotBeNull();

        var listedLayer = listApi.Data!.Single(layer => layer.LayerId == _layerId);
        listedLayer.Enabled.Should().BeTrue();

        var toggleRequest = new LayerEnabledRequest { Enabled = false };

        var toggleResponse = await _client.PutAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers/{_layerId}/enabled?serviceName={_serviceName}",
            JsonContent.Create(toggleRequest, options: _jsonOptions));

        toggleResponse.Be200Ok();

        var togglePayload = await toggleResponse.Content.ReadAsStringAsync();
        var toggleApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(togglePayload, _jsonOptions);

        toggleApi.Should().NotBeNull();
        toggleApi!.Success.Should().BeTrue();
        toggleApi.Data.Should().NotBeNull();
        toggleApi.Data!.Enabled.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PublishLayer_FeatureServerQuery_ReadsSourceBackedPostGisTable()
    {
        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer publish FeatureServer query integration test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var publishResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        var publishPayload = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {publishPayload}");
        var publishApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(publishPayload, _jsonOptions);
        publishApi.Should().NotBeNull();
        publishApi!.Data.Should().NotBeNull();
        _layerId = publishApi.Data!.LayerId;
        await MirrorPublishedLayerMetadataV2Async(_layerId.Value, _serviceName);

        using (var scope = _fixture.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ILayerCatalog>();
            var service = await catalog.GetServiceAsync(_serviceName);
            service.Should().NotBeNull();
            service!.ConnectionId.Should().Be(_connectionId);
            var layer = service.Layers.Single(layer => layer.Id == _layerId);
            layer.StorageMapping.Should().NotBeNull();
            layer.StorageMapping!.IsSourceBacked.Should().BeTrue();

            var router = scope.ServiceProvider.GetRequiredService<FeatureProviderQueryRouter>();
            var reader = await router.ResolveReaderAsync(
                service,
                layer,
                FeatureProviderReadOperation.Query);
            var directResult = await reader.QueryAsync(
                _layerId.Value,
                new FeatureQuery { Where = "1=1", Limit = 10, SpatialReferenceSrid = 4326 });

            directResult.Items.Should().HaveCount(1);
        }

        var metadataResponse = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");

        metadataResponse.Be200Ok();
        using (var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync()))
        {
            metadata.RootElement.TryGetProperty("extent", out var extent).Should().BeTrue();
            extent.GetProperty("xmin").GetDouble().Should().Be(1d);
            extent.GetProperty("ymin").GetDouble().Should().Be(1d);
            extent.GetProperty("xmax").GetDouble().Should().Be(1d);
            extent.GetProperty("ymax").GetDouble().Should().Be(1d);
        }

        var queryResponse = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}/query?f=json&where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=10");

        var queryPayload = await queryResponse.Content.ReadAsStringAsync();
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {queryPayload}");
        using var queryDocument = JsonDocument.Parse(queryPayload);
        var features = queryDocument.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);

        var attributes = features[0].GetProperty("attributes");
        attributes.GetProperty("id").GetInt64().Should().Be(1);
        attributes.GetProperty("name").GetString().Should().Be("Test Feature");
        attributes.GetProperty("population").GetInt32().Should().Be(100);

        var geometry = features[0].GetProperty("geometry");
        geometry.GetProperty("x").GetDouble().Should().Be(1d);
        geometry.GetProperty("y").GetDouble().Should().Be(1d);

        using var ogcClient = _fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Remove("X-Honua-Test-Schema"));

        var ogcItemsResponse = await ogcClient.GetAsync(
            $"/ogc/features/collections/{_layerId}/items?f=json&limit=10");

        var ogcItemsPayload = await ogcItemsResponse.Content.ReadAsStringAsync();
        ogcItemsResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {ogcItemsPayload}");
        using var ogcItemsDocument = JsonDocument.Parse(ogcItemsPayload);
        var ogcFeatures = ogcItemsDocument.RootElement.GetProperty("features");
        ogcFeatures.GetArrayLength().Should().Be(1, $"response: {ogcItemsPayload}");

        var ogcFeature = ogcFeatures[0];
        ogcFeature.GetProperty("id").GetInt64().Should().Be(1);
        ogcFeature.GetProperty("properties").GetProperty("name").GetString().Should().Be("Test Feature");
        ogcFeature.GetProperty("properties").GetProperty("population").GetInt32().Should().Be(100);
        var coordinates = ogcFeature.GetProperty("geometry").GetProperty("coordinates");
        coordinates[0].GetDouble().Should().Be(1d);
        coordinates[1].GetDouble().Should().Be(1d);

        var ogcItemResponse = await ogcClient.GetAsync(
            $"/ogc/features/collections/{_layerId}/items/1?f=json");

        var ogcItemPayload = await ogcItemResponse.Content.ReadAsStringAsync();
        ogcItemResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {ogcItemPayload}");
        using var ogcItemDocument = JsonDocument.Parse(ogcItemPayload);
        ogcItemDocument.RootElement.GetProperty("id").GetInt64().Should().Be(1);
        ogcItemDocument.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("Test Feature");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PublishLayer_ByConnectionName_RoutesStorageMappedFeatureServerQueries()
    {
        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer publish connection-name routing integration test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var publishResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionName}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        var publishPayload = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {publishPayload}");
        var publishApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(publishPayload, _jsonOptions);
        publishApi.Should().NotBeNull();
        publishApi!.Data.Should().NotBeNull();
        _layerId = publishApi.Data!.LayerId;

        using (var scope = _fixture.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ILayerCatalog>();
            var service = await catalog.GetServiceAsync(_serviceName);
            service.Should().NotBeNull();
            service!.ConnectionId.Should().BeNull();
            var layer = service.Layers.Single(layer => layer.Id == _layerId);
            layer.StorageMapping.Should().NotBeNull();
            layer.StorageMapping!.IsSourceBacked.Should().BeTrue();
        }

        await InsertPostGisFeatureAsync("Name Routed Feature", 200, 2d, 2d);

        var queryResponse = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}/query?f=json&where=1%3D1&outFields=name,population&returnGeometry=false&resultRecordCount=10");

        var queryPayload = await queryResponse.Content.ReadAsStringAsync();
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {queryPayload}");
        using var queryDocument = JsonDocument.Parse(queryPayload);
        var names = queryDocument.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString())
            .ToArray();

        names.Should().Contain("Test Feature");
        names.Should().Contain("Name Routed Feature");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PublishLayer_FeatureServerNearestNeighbor_OrdersSourceBackedRows()
    {
        await InsertPostGisFeatureAsync("Near Feature", 200, 1.01d, 1.01d);
        await InsertPostGisFeatureAsync("Far Feature", 300, 10d, 10d);

        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer publish nearest-neighbor integration test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var publishResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        var publishPayload = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {publishPayload}");
        var publishApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(publishPayload, _jsonOptions);
        publishApi.Should().NotBeNull();
        publishApi!.Data.Should().NotBeNull();
        _layerId = publishApi.Data!.LayerId;

        var pointGeometry = Uri.EscapeDataString(@"{""x"":1,""y"":1}");
        var queryResponse = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}/query?f=json&geometry={pointGeometry}&nearestCount=2&returnDistance=true&outFields=*&returnGeometry=false");

        var queryPayload = await queryResponse.Content.ReadAsStringAsync();
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {queryPayload}");
        using var queryDocument = JsonDocument.Parse(queryPayload);
        var features = queryDocument.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(2);

        var firstAttributes = features[0].GetProperty("attributes");
        var secondAttributes = features[1].GetProperty("attributes");

        firstAttributes.GetProperty("name").GetString().Should().Be("Test Feature");
        secondAttributes.GetProperty("name").GetString().Should().Be("Near Feature");
        firstAttributes.GetProperty("distance").GetDouble().Should().BeLessThanOrEqualTo(secondAttributes.GetProperty("distance").GetDouble());
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/enabled")]
    public async Task PublishLayer_BulkToggle_DisablesServiceLayers()
    {
        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer bulk toggle integration test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var publishResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        publishResponse.Be201Created();
        var publishPayload = await publishResponse.Content.ReadAsStringAsync();
        var publishApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(publishPayload, _jsonOptions);
        publishApi.Should().NotBeNull();
        publishApi!.Success.Should().BeTrue();
        publishApi.Data.Should().NotBeNull();

        _layerId = publishApi.Data!.LayerId;

        var bulkRequest = new LayerEnabledRequest { Enabled = false };

        var bulkResponse = await _client.PutAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers/enabled?serviceName={_serviceName}",
            JsonContent.Create(bulkRequest, options: _jsonOptions));

        bulkResponse.Be200Ok();

        var bulkPayload = await bulkResponse.Content.ReadAsStringAsync();
        var bulkApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary[]>>(bulkPayload, _jsonOptions);

        bulkApi.Should().NotBeNull();
        bulkApi!.Success.Should().BeTrue();
        bulkApi.Data.Should().NotBeNull();

        bulkApi.Data!.Single(layer => layer.LayerId == _layerId).Enabled.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task PublishLayer_MaterializesFeaturesAndExtentForFeatureServer()
    {
        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer publish materialization integration test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var publishResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        var publishPayload = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {publishPayload}");
        var publishApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(publishPayload, _jsonOptions);
        publishApi.Should().NotBeNull();
        publishApi!.Success.Should().BeTrue();
        publishApi.Data.Should().NotBeNull();
        _layerId = publishApi.Data!.LayerId;
        await MirrorPublishedLayerMetadataV2Async(_layerId.Value, _serviceName);

        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    COUNT(*)::int,
                    MIN(attributes->>'id'),
                    MIN(attributes->>'name'),
                    MIN((attributes->>'population')::int),
                    ST_XMin(l.extent),
                    ST_YMin(l.extent),
                    ST_XMax(l.extent),
                    ST_YMax(l.extent),
                    ST_SRID(l.extent)
                FROM features f
                INNER JOIN honua.layers l
                    ON l.layer_id = f.layer_id
                WHERE f.layer_id = @layerId
                GROUP BY l.extent;
                """;
            command.Parameters.AddWithValue("layerId", _layerId.Value);

            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(1);
            reader.GetString(1).Should().Be("1");
            reader.GetString(2).Should().Be("Test Feature");
            reader.GetInt32(3).Should().Be(100);
            reader.GetDouble(4).Should().Be(1);
            reader.GetDouble(5).Should().Be(1);
            reader.GetDouble(6).Should().Be(1);
            reader.GetDouble(7).Should().Be(1);
            reader.GetInt32(8).Should().Be(4326);
        }

        using var featureClient = _fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Remove("X-Honua-Test-Schema"));

        var queryResponse = await featureClient.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}/query?where=1%3D1&f=json");

        queryResponse.Be200Ok();
        var queryPayload = await queryResponse.Content.ReadAsStringAsync();
        using (var queryDocument = JsonDocument.Parse(queryPayload))
        {
            var features = queryDocument.RootElement.GetProperty("features").EnumerateArray().ToArray();
            features.Should().ContainSingle();
            var attributes = features[0].GetProperty("attributes");
            attributes.GetProperty("id").GetInt32().Should().Be(1);
            attributes.GetProperty("name").GetString().Should().Be("Test Feature");
            attributes.GetProperty("population").GetInt32().Should().Be(100);
        }

        var metadataResponse = await featureClient.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}");

        metadataResponse.Be200Ok();
        var metadataPayload = await metadataResponse.Content.ReadAsStringAsync();
        using var metadataDocument = JsonDocument.Parse(metadataPayload);
        var extent = metadataDocument.RootElement.GetProperty("extent");
        extent.GetProperty("xmin").GetDouble().Should().Be(1);
        extent.GetProperty("ymin").GetDouble().Should().Be(1);
        extent.GetProperty("xmax").GetDouble().Should().Be(1);
        extent.GetProperty("ymax").GetDouble().Should().Be(1);
        extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers/extents/refresh")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnExtentOnly=true")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task RefreshLayerExtents_WithProjectedSource_RecomputesLayerAndServiceExtentInWgs84()
    {
        const double firstLon = -157.8583;
        const double firstLat = 21.3069;
        const double secondLon = -157.9225;
        const double secondLat = 21.3187;
        const double tolerance = 0.0001;
        var projectedTable = $"layer_3857_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync(FormattableString.Invariant($"""
                CREATE TABLE public.{projectedTable} (
                    id integer PRIMARY KEY,
                    name text NOT NULL,
                    population integer,
                    geom geometry(Point, 3857) NOT NULL
                );

                INSERT INTO public.{projectedTable} (id, name, population, geom)
                VALUES (
                    1,
                    'Projected Feature',
                    100,
                    ST_Transform(ST_SetSRID(ST_Point({firstLon}, {firstLat}), 4326), 3857)
                );
                """));

            var publishRequest = new PublishLayerRequest
            {
                Schema = _schema,
                Table = projectedTable,
                LayerName = $"Layer {projectedTable}",
                Description = "Layer publish projected extent refresh integration test",
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = new[] { "id", "name", "population" },
                ServiceName = _serviceName,
                Enabled = true
            };

            var publishedLayer = await PublishLayerAsync(publishRequest);
            _layerId = publishedLayer.LayerId;

            await _fixture.Postgres.ExecuteAsync(FormattableString.Invariant($"""
                INSERT INTO public.{projectedTable} (id, name, population, geom)
                VALUES (
                    2,
                    'Projected Feature 2',
                    200,
                    ST_Transform(ST_SetSRID(ST_Point({secondLon}, {secondLat}), 4326), 3857)
                );

                UPDATE honua.layers
                SET extent = NULL
                WHERE layer_id = {_layerId.Value};

                UPDATE honua.services
                SET service_extent = NULL
                WHERE service_name = '{_serviceName}';
                """));

            var refreshResponse = await _client.PostAsync(
                $"/api/v1/admin/connections/{_connectionId}/layers/extents/refresh?serviceName={_serviceName}",
                content: null);

            var refreshPayload = await refreshResponse.Content.ReadAsStringAsync();
            refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {refreshPayload}");
            var refreshApi = JsonSerializer.Deserialize<ApiResponse<LayerExtentRefreshResult>>(
                refreshPayload,
                _jsonOptions);

            refreshApi.Should().NotBeNull();
            refreshApi!.Success.Should().BeTrue();
            refreshApi.Data.Should().NotBeNull();
            refreshApi.Data!.RefreshedLayerCount.Should().Be(1);
            refreshApi.Data.LayersWithExtent.Should().Be(1);
            refreshApi.Data.Layers.Single().ExtentSrid.Should().Be(4326);

            await MirrorPublishedLayerMetadataV2Async(_layerId.Value, _serviceName);
            await AssertPersistedExtentAsync(secondLon, firstLat, firstLon, secondLat, tolerance);
            await AssertFeatureServerExtentAsync(secondLon, firstLat, firstLon, secondLat, tolerance);
            await AssertFeatureServerQueryExtentAsync(secondLon, firstLat, firstLon, secondLat, tolerance);
            await AssertOgcCollectionExtentAsync(secondLon, firstLat, firstLon, secondLat, tolerance);
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"DROP TABLE IF EXISTS public.{projectedTable};");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers/extents/refresh")]
    public async Task RefreshLayerExtents_WithEnabledEmptyLayer_NullsLayerExtentAndKeepsServiceExtentFromNonEmptyLayer()
    {
        var emptyTable = $"layer_empty_{Guid.NewGuid():N}";
        var disabledTable = $"layer_disabled_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync($"""
                CREATE TABLE public.{emptyTable} (
                    id SERIAL PRIMARY KEY,
                    name TEXT NOT NULL,
                    population INTEGER,
                    geom geometry(Point, 4326) NOT NULL
                );

                INSERT INTO public.{emptyTable} (name, population, geom)
                VALUES ('Empty Candidate', 200, ST_SetSRID(ST_Point(10, 10), 4326));

                CREATE TABLE public.{disabledTable} (
                    id SERIAL PRIMARY KEY,
                    name TEXT NOT NULL,
                    population INTEGER,
                    geom geometry(Point, 4326) NOT NULL
                );

                INSERT INTO public.{disabledTable} (name, population, geom)
                VALUES ('Disabled Candidate', 300, ST_SetSRID(ST_Point(20, 20), 4326));
                """);

            var nonEmptyLayer = await PublishLayerAsync(new PublishLayerRequest
            {
                Schema = _schema,
                Table = _tableName,
                LayerName = $"Layer {_tableName}",
                Description = "Layer publish non-empty extent refresh integration test",
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = _idNamePopulationFields,
                ServiceName = _serviceName,
                Enabled = true
            });
            _layerId = nonEmptyLayer.LayerId;

            var emptyLayer = await PublishLayerAsync(new PublishLayerRequest
            {
                Schema = _schema,
                Table = emptyTable,
                LayerName = $"Layer {emptyTable}",
                Description = "Layer publish empty extent refresh integration test",
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = _idNamePopulationFields,
                ServiceName = _serviceName,
                Enabled = true
            });

            var disabledLayer = await PublishLayerAsync(new PublishLayerRequest
            {
                Schema = _schema,
                Table = disabledTable,
                LayerName = $"Layer {disabledTable}",
                Description = "Layer publish disabled extent refresh integration test",
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = _idNamePopulationFields,
                ServiceName = _serviceName,
                Enabled = false
            });

            await _fixture.Postgres.ExecuteAsync($"""
                DELETE FROM public.{emptyTable};

                UPDATE honua.layers
                SET extent = ST_MakeEnvelope(10, 10, 10, 10, 4326)
                WHERE layer_id = {emptyLayer.LayerId};

                UPDATE honua.services
                SET service_extent = ST_MakeEnvelope(20, 20, 20, 20, 4326)
                WHERE service_name = '{_serviceName}';
                """);

            var refreshResponse = await _client.PostAsync(
                $"/api/v1/admin/connections/{_connectionId}/layers/extents/refresh?serviceName={_serviceName}",
                content: null);

            var refreshPayload = await refreshResponse.Content.ReadAsStringAsync();
            refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {refreshPayload}");
            var refreshApi = JsonSerializer.Deserialize<ApiResponse<LayerExtentRefreshResult>>(
                refreshPayload,
                _jsonOptions);

            refreshApi.Should().NotBeNull();
            refreshApi!.Success.Should().BeTrue();
            refreshApi.Data.Should().NotBeNull();
            refreshApi.Data!.RefreshedLayerCount.Should().Be(3);
            refreshApi.Data.LayersWithExtent.Should().Be(2);
            refreshApi.Data.LayersWithoutExtent.Should().Be(1);
            refreshApi.Data.ServiceExtentUpdated.Should().BeTrue();

            var emptyLayerResult = refreshApi.Data.Layers.Single(layer => layer.LayerId == emptyLayer.LayerId);
            emptyLayerResult.HasExtent.Should().BeFalse();
            emptyLayerResult.ExtentSrid.Should().BeNull();

            var nonEmptyLayerResult = refreshApi.Data.Layers.Single(layer => layer.LayerId == nonEmptyLayer.LayerId);
            nonEmptyLayerResult.HasExtent.Should().BeTrue();
            nonEmptyLayerResult.ExtentSrid.Should().Be(4326);

            var disabledLayerResult = refreshApi.Data.Layers.Single(layer => layer.LayerId == disabledLayer.LayerId);
            disabledLayerResult.HasExtent.Should().BeTrue();
            disabledLayerResult.ExtentSrid.Should().Be(4326);

            await AssertLayerExtentIsNullAsync(emptyLayer.LayerId);
            await AssertPersistedLayerAndServiceExtentAsync(nonEmptyLayer.LayerId, 1, 1, 1, 1, 0);
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"""
                DROP TABLE IF EXISTS public.{emptyTable};
                DROP TABLE IF EXISTS public.{disabledTable};
                """);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_PrimaryKeyNotInFields_ReturnsBadRequest()
    {
        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer publish integration test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var response = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var payload = await response.Content.ReadAsStringAsync();
        var api = JsonSerializer.Deserialize<ApiResponse<object>>(payload, _jsonOptions);
        api.Should().NotBeNull();
        api!.Success.Should().BeFalse();
        api.Message.Should().Contain("Primary key field 'id' must be included in selected fields.");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/tables/validate")]
    public async Task ValidateTableForPublish_WithValidTable_ReturnsFieldMetadata()
    {
        var validationRequest = new ValidateTablePublishRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            ServiceName = _serviceName,
            TargetSrid = 4326,
            GeometryColumn = "geom",
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" }
        };

        var response = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/tables/validate",
            JsonContent.Create(validationRequest, options: _jsonOptions));

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");
        var api = JsonSerializer.Deserialize<ApiResponse<TablePublishValidationResult>>(payload, _jsonOptions);

        api.Should().NotBeNull();
        api!.Success.Should().BeTrue();
        api.Data.Should().NotBeNull();
        api.Data!.IsValid.Should().BeTrue();
        api.Data.Status.Should().Be("valid");
        api.Data.Schema.Should().Be(_schema);
        api.Data.Table.Should().Be(_tableName);
        api.Data.GeometryColumn.Should().Be("geom");
        api.Data.PrimaryKey.Should().Be("id");
        api.Data.ObjectIdStrategy.Should().Be("source-integer-primary-key");
        api.Data.SourceSrid.Should().Be(4326);
        api.Data.TargetSrid.Should().Be(4326);
        api.Data.FeatureCount.Should().Be(1);
        api.Data.Fields.Should().Contain(field => field.Name == "id" && field.IsPrimaryKey && field.IsSelected);
        api.Data.Fields.Should().OnlyContain(field => field.Name != "geom");
        api.Data.Checks.Should().Contain(check => check.Code == "invalid-geometries" && check.Severity == "pass");
        api.Data.Checks.Should().NotContain(check => check.Severity == "error");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/tables/validate")]
    public async Task ValidateTableForPublish_WithTextPrimaryKey_ReturnsStructuredError()
    {
        var textPrimaryKeyTable = $"layer_textpk_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync($"""
                CREATE TABLE public.{textPrimaryKeyTable} (
                    code text PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry(Point, 4326) NOT NULL
                );

                INSERT INTO public.{textPrimaryKeyTable} (code, name, geom)
                VALUES ('A-1', 'Text key feature', ST_SetSRID(ST_Point(1, 1), 4326));
                """);

            var validationRequest = new ValidateTablePublishRequest
            {
                Schema = _schema,
                Table = textPrimaryKeyTable,
                LayerName = $"Layer {textPrimaryKeyTable}",
                ServiceName = _serviceName,
                TargetSrid = 4326,
                GeometryColumn = "geom",
                PrimaryKey = "code",
                Fields = new[] { "code", "name" }
            };

            var response = await _client.PostAsync(
                $"/api/v1/admin/connections/{_connectionId}/tables/validate",
                JsonContent.Create(validationRequest, options: _jsonOptions));

            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");
            var api = JsonSerializer.Deserialize<ApiResponse<TablePublishValidationResult>>(payload, _jsonOptions);

            api.Should().NotBeNull();
            api!.Success.Should().BeTrue();
            api.Data.Should().NotBeNull();
            api.Data!.IsValid.Should().BeFalse();
            api.Data.Status.Should().Be("invalid");
            api.Data.ObjectIdStrategy.Should().Be("unsupported-source-primary-key");
            api.Data.Checks.Should().Contain(check =>
                check.Code == "primary-key-type" &&
                check.Severity == "error" &&
                check.Actual == "text");
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"DROP TABLE IF EXISTS public.{textPrimaryKeyTable};");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/tables/validate")]
    public async Task ValidateTableForPublish_WithInvalidGeometry_ReturnsStructuredError()
    {
        var invalidGeometryTable = $"layer_invalidgeom_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync($"""
                CREATE TABLE public.{invalidGeometryTable} (
                    id integer PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry(Polygon, 4326) NOT NULL
                );

                INSERT INTO public.{invalidGeometryTable} (id, name, geom)
                VALUES (1, 'Invalid polygon', ST_GeomFromText('POLYGON((0 0, 1 1, 1 0, 0 1, 0 0))', 4326));
                """);

            var result = await ValidateTableForPublishAsync(new ValidateTablePublishRequest
            {
                Schema = _schema,
                Table = invalidGeometryTable,
                LayerName = $"Layer {invalidGeometryTable}",
                ServiceName = _serviceName,
                TargetSrid = 4326,
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Fields = _idNameFields
            });

            result.IsValid.Should().BeFalse();
            result.Status.Should().Be("invalid");
            result.InvalidGeometryCount.Should().Be(1);
            result.Checks.Should().Contain(check =>
                check.Code == "invalid-geometries" &&
                check.Severity == "error" &&
                check.Actual == "1");
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"DROP TABLE IF EXISTS public.{invalidGeometryTable};");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_WithInvalidGeometry_ReturnsBadRequestAndDoesNotCreateLayer()
    {
        var invalidGeometryTable = $"layer_publish_invalidgeom_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync($"""
                CREATE TABLE public.{invalidGeometryTable} (
                    id integer PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry(Polygon, 4326) NOT NULL
                );

                INSERT INTO public.{invalidGeometryTable} (id, name, geom)
                VALUES (1, 'Invalid polygon', ST_GeomFromText('POLYGON((0 0, 1 1, 1 0, 0 1, 0 0))', 4326));
                """);

            var publishRequest = new PublishLayerRequest
            {
                Schema = _schema,
                Table = invalidGeometryTable,
                LayerName = $"Layer {invalidGeometryTable}",
                GeometryColumn = "geom",
                GeometryType = "Polygon",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = _idNameFields,
                ServiceName = _serviceName,
                Enabled = true
            };

            var response = await _client.PostAsync(
                $"/api/v1/admin/connections/{_connectionId}/layers",
                JsonContent.Create(publishRequest, options: _jsonOptions));

            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"response: {payload}");
            payload.Should().Contain("invalid geometries");
            (await LayerMetadataExistsAsync(invalidGeometryTable)).Should().BeFalse();
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"DROP TABLE IF EXISTS public.{invalidGeometryTable};");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/tables/validate")]
    public async Task ValidateTableForPublish_WithTableWithoutGeometry_ReturnsGeometryColumnError()
    {
        var noGeometryTable = $"layer_nogeom_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync($"""
                CREATE TABLE public.{noGeometryTable} (
                    id integer PRIMARY KEY,
                    name text NOT NULL
                );

                INSERT INTO public.{noGeometryTable} (id, name)
                VALUES (1, 'No geometry');
                """);

            var result = await ValidateTableForPublishAsync(new ValidateTablePublishRequest
            {
                Schema = _schema,
                Table = noGeometryTable,
                LayerName = $"Layer {noGeometryTable}",
                ServiceName = _serviceName,
                TargetSrid = 4326,
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Fields = _idNameFields
            });

            result.IsValid.Should().BeFalse();
            result.Fields.Should().Contain(field => field.Name == "id" && field.IsPrimaryKey);
            result.Fields.Should().Contain(field => field.Name == "name");
            result.Checks.Should().Contain(check => check.Code == "geometry-column" && check.Severity == "error");
            result.Checks.Should().NotContain(check => check.Code == "source-table" && check.Severity == "error");
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"DROP TABLE IF EXISTS public.{noGeometryTable};");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/tables/validate")]
    public async Task ValidateTableForPublish_WithNon4326Srid_ReturnsTransformWarning()
    {
        var mercatorTable = $"layer_srid3857_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync($"""
                CREATE TABLE public.{mercatorTable} (
                    id integer PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry(Point, 3857) NOT NULL
                );

                INSERT INTO public.{mercatorTable} (id, name, geom)
                VALUES (1, 'Mercator point', ST_SetSRID(ST_Point(111319.49079327357, 0), 3857));
                """);

            var result = await ValidateTableForPublishAsync(new ValidateTablePublishRequest
            {
                Schema = _schema,
                Table = mercatorTable,
                LayerName = $"Layer {mercatorTable}",
                ServiceName = _serviceName,
                TargetSrid = 4326,
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Fields = _idNameFields
            });

            result.IsValid.Should().BeTrue();
            result.Status.Should().Be("warning");
            result.SourceSrid.Should().Be(3857);
            result.TargetSrid.Should().Be(4326);
            result.Checks.Should().Contain(check =>
                check.Code == "source-srid-transform" &&
                check.Severity == "warning" &&
                check.Expected == "4326" &&
                check.Actual == "3857");
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"DROP TABLE IF EXISTS public.{mercatorTable};");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/tables/validate")]
    public async Task ValidateTableForPublish_WithMixedGeometryAndSrid_ReturnsStructuredErrors()
    {
        var mixedTable = $"layer_mixedgeom_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync($"""
                CREATE TABLE public.{mixedTable} (
                    id integer PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry(Geometry) NOT NULL
                );

                INSERT INTO public.{mixedTable} (id, name, geom)
                VALUES
                    (1, 'Point', ST_SetSRID(ST_Point(0, 0), 4326)),
                    (2, 'Line', ST_SetSRID(ST_MakeLine(ST_Point(0, 0), ST_Point(1, 1)), 3857));
                """);

            var result = await ValidateTableForPublishAsync(new ValidateTablePublishRequest
            {
                Schema = _schema,
                Table = mixedTable,
                LayerName = $"Layer {mixedTable}",
                ServiceName = _serviceName,
                TargetSrid = 4326,
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Fields = _idNameFields
            });

            result.IsValid.Should().BeFalse();
            result.Checks.Should().Contain(check => check.Code == "mixed-geometry" && check.Severity == "error");
            result.Checks.Should().Contain(check => check.Code == "mixed-srid" && check.Severity == "error");
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"DROP TABLE IF EXISTS public.{mixedTable};");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/tables/validate")]
    public async Task ValidateTableForPublish_WithEmptyTable_ReturnsFeatureCountError()
    {
        var emptyTable = $"layer_empty_{Guid.NewGuid():N}";

        try
        {
            await _fixture.Postgres.ExecuteAsync($"""
                CREATE TABLE public.{emptyTable} (
                    id integer PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry(Point, 4326) NOT NULL
                );
                """);

            var result = await ValidateTableForPublishAsync(new ValidateTablePublishRequest
            {
                Schema = _schema,
                Table = emptyTable,
                LayerName = $"Layer {emptyTable}",
                ServiceName = _serviceName,
                TargetSrid = 4326,
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Fields = _idNameFields
            });

            result.IsValid.Should().BeFalse();
            result.FeatureCount.Should().Be(0);
            result.Checks.Should().Contain(check => check.Code == "feature-count" && check.Severity == "error");
        }
        finally
        {
            await _fixture.Postgres.ExecuteAsync($"DROP TABLE IF EXISTS public.{emptyTable};");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_WhenLayerTableIsEmpty_ReturnsCreated()
    {
        await ResetPublishedMetadataAsync();

        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer publish sequence bootstrap integration test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var publishResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        var publishPayload = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {publishPayload}");
        var publishApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(publishPayload, _jsonOptions);

        publishApi.Should().NotBeNull();
        publishApi!.Success.Should().BeTrue();
        publishApi.Data.Should().NotBeNull();

        _layerId = publishApi.Data!.LayerId;
        _layerId.Value.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_WithExternalConnectionWithoutMirroredRegistryRecord_ReturnsCreated()
    {
        var externalDatabaseName = await CreateConnectionDatabaseAsync();
        var externalTableName = $"layer_ext_{Guid.NewGuid():N}";
        var externalServiceName = $"svc_ext_{Guid.NewGuid():N}";
        var externalConnectionId = Guid.Empty;

        try
        {
            var externalConnectionString = BuildConnectionString(externalDatabaseName);
            await CreatePostGisTableAsync(externalConnectionString, externalTableName);
            externalConnectionId = await CreateTransientSecureConnectionAsync(
                externalConnectionString,
                $"layer-publish-external-{Guid.NewGuid():N}");

            var publishRequest = new PublishLayerRequest
            {
                Schema = PublishSchema,
                Table = externalTableName,
                LayerName = $"Layer {externalTableName}",
                Description = "Layer publish external database integration test",
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = new[] { "id", "name", "population" },
                ServiceName = externalServiceName,
                Enabled = true
            };

            var publishResponse = await _client.PostAsync(
                $"/api/v1/admin/connections/{externalConnectionId}/layers",
                JsonContent.Create(publishRequest, options: _jsonOptions));

            var publishPayload = await publishResponse.Content.ReadAsStringAsync();
            publishResponse.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {publishPayload}");
        }
        finally
        {
            if (externalConnectionId != Guid.Empty)
            {
                await DeleteSecureConnectionAsync(externalConnectionId);
            }

            await DropConnectionDatabaseAsync(externalDatabaseName);
        }
    }

    private async Task<TablePublishValidationResult> ValidateTableForPublishAsync(ValidateTablePublishRequest request)
    {
        var response = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/tables/validate",
            JsonContent.Create(request, options: _jsonOptions));

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");
        var api = JsonSerializer.Deserialize<ApiResponse<TablePublishValidationResult>>(payload, _jsonOptions);

        api.Should().NotBeNull();
        api!.Success.Should().BeTrue();
        api.Data.Should().NotBeNull();
        return api.Data!;
    }

    private async Task<PublishedLayerSummary> PublishLayerAsync(PublishLayerRequest request)
    {
        var response = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request, options: _jsonOptions));

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"response: {payload}");
        var api = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(payload, _jsonOptions);

        api.Should().NotBeNull();
        api!.Success.Should().BeTrue();
        api.Data.Should().NotBeNull();
        await MirrorPublishedLayerMetadataV2Async(api.Data!.LayerId, api.Data.ServiceName);
        return api.Data!;
    }

    private async Task MirrorPublishedLayerMetadataV2Async(int layerId, string serviceName)
    {
        var provider = _fixture.GetService<IMetadataV2GraphProvider>() as TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException("Test V2 graph provider was not registered.");

        var (layerName, description, primaryKey, geometryColumn, geometryType, srid, storageSrid, bbox) =
            await ReadLayerMetadataForV2MirrorAsync(layerId);
        var fields = await ReadLayerFieldsForV2MirrorAsync(layerId, primaryKey);

        var snapshot = await provider.GetCurrentAsync();
        var graph = snapshot.Graph;
        var serviceId = $"svc-published-{serviceName}";
        var resourceId = $"res-published-layer-{layerId}";
        var bindingId = $"binding-published-layer-{layerId}";
        var publicationId = $"pub-published-layer-{layerId}";
        var replacedResourceIds = graph.Publications
            .Where(publication => publication.LayerIndex == layerId)
            .Select(publication => publication.ResourceId)
            .Concat(graph.StorageBindings
                .Where(binding => binding.StorageLayerId == layerId)
                .Select(binding => binding.ResourceId))
            .Append(resourceId)
            .ToHashSet(StringComparer.Ordinal);

        var resources = graph.Resources.Where(resource =>
                !replacedResourceIds.Contains(resource.Metadata.Id))
            .Append(new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = resourceId,
                    Name = layerName,
                    Description = description
                },
                Type = MetadataV2ResourceType.FeatureDataset,
                Status = ReadyMetadataV2Status(),
                StorageBindingIds = [bindingId],
                PrimaryStorageBindingId = bindingId,
                SchemaFields = fields,
                Spatial = new MetadataV2ResourceSpatial
                {
                    SpatialReference = ToSpatialReference(srid),
                    StorageCrs = storageSrid > 0 && storageSrid != srid ? ToSpatialReference(storageSrid) : null,
                    GeometryType = ToMetadataV2GeometryType(geometryType),
                    PrimaryGeometryField = geometryColumn,
                    Bbox = bbox,
                    SupportedCrs = storageSrid > 0 && storageSrid != srid
                        ? [ToSpatialReference(srid), ToSpatialReference(storageSrid)]
                        : [ToSpatialReference(srid)]
                },
            })
            .ToArray();

        var storageBindings = graph.StorageBindings.Where(binding =>
                !string.Equals(binding.Metadata.Id, bindingId, StringComparison.Ordinal) &&
                binding.StorageLayerId != layerId)
            .Append(new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = bindingId, Name = bindingId },
                ResourceId = resourceId,
                StorageType = MetadataV2StorageType.RelationalTable,
                Locator = $"features:{layerId}",
                StorageLayerId = layerId,
            })
            .ToArray();

        var publications = graph.Publications.Where(publication =>
                !string.Equals(publication.Metadata.Id, publicationId, StringComparison.Ordinal) &&
                publication.LayerIndex != layerId)
            .Append(new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = publicationId,
                    Name = layerId.ToString(CultureInfo.InvariantCulture)
                },
                ServiceId = serviceId,
                ResourceId = resourceId,
                PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
                Status = ReadyMetadataV2Status(),
                Identifier = new MetadataV2PublicationIdentifier
                {
                    Value = layerId.ToString(CultureInfo.InvariantCulture),
                    IsNumeric = true,
                },
                IsPrimary = true,
            })
            .ToArray();

        var publicationIds = publications
            .Where(publication => string.Equals(publication.ServiceId, serviceId, StringComparison.Ordinal))
            .Select(publication => publication.Metadata.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var services = graph.Services.Where(service =>
                !string.Equals(service.Metadata.Id, serviceId, StringComparison.Ordinal))
            .Append(new MetadataV2Service
            {
                Metadata = new MetadataV2ObjectMetadata { Id = serviceId, Name = serviceName },
                ServiceType = MetadataV2ServiceType.EsriFeatureService,
                Protocols = [ServiceProtocols.FeatureServer, ServiceProtocols.OgcFeatures],
                PublicationIds = publicationIds,
                Status = ReadyMetadataV2Status(),
                SpatialReference = ToSpatialReference(srid),
            })
            .ToArray();

        provider.SetGraph(graph with
        {
            Resources = resources,
            StorageBindings = storageBindings,
            Services = services,
            Publications = publications,
            Revision = graph.Revision + 1,
            GeneratedAt = DateTimeOffset.UtcNow,
        });
    }

    private async Task<(
        string LayerName,
        string? Description,
        string PrimaryKey,
        string GeometryColumn,
        string GeometryType,
        int Srid,
        int StorageSrid,
        MetadataV2Bbox? Bbox)> ReadLayerMetadataForV2MirrorAsync(int layerId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                layer_name,
                description,
                primary_key_column,
                geometry_column,
                geometry_type,
                srid,
                COALESCE(storage_srid, srid),
                ST_XMin(extent),
                ST_YMin(extent),
                ST_XMax(extent),
                ST_YMax(extent)
            FROM honua.layers
            WHERE layer_id = @layerId;
            """;
        command.Parameters.AddWithValue("layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        var bbox = reader.IsDBNull(7)
            ? null
            : new MetadataV2Bbox
            {
                West = reader.GetDouble(7),
                South = reader.GetDouble(8),
                East = reader.GetDouble(9),
                North = reader.GetDouble(10),
            };

        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            bbox);
    }

    private async Task<IReadOnlyList<MetadataV2Field>> ReadLayerFieldsForV2MirrorAsync(
        int layerId,
        string primaryKey)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT field_name, field_type, nullable, max_length, description
            FROM honua.layer_fields
            WHERE layer_id = @layerId
            ORDER BY field_order;
            """;
        command.Parameters.AddWithValue("layerId", layerId);

        var fields = new List<MetadataV2Field>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var fieldName = reader.GetString(0);
            var fieldType = ToMetadataV2FieldType(reader.GetString(1));
            var semanticRoles = new List<string>();
            if (string.Equals(fieldName, primaryKey, StringComparison.OrdinalIgnoreCase))
            {
                semanticRoles.Add("id.primary");
            }
            if (fieldType is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
            {
                semanticRoles.Add("geometry.primary");
            }

            fields.Add(new MetadataV2Field
            {
                Name = fieldName,
                Type = fieldType,
                Nullable = reader.GetBoolean(2),
                Length = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                Editable = fieldType is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography),
                SemanticRoles = semanticRoles,
            });
        }

        return fields;
    }

    private static MetadataV2Status ReadyMetadataV2Status()
        => new()
        {
            Lifecycle = MetadataV2LifecycleStatus.Active,
            State = MetadataV2OperationalState.Ready,
        };

    private static MetadataV2SpatialReference ToSpatialReference(int srid)
        => new()
        {
            Srid = srid,
            Crs = $"EPSG:{srid}",
            IsGeographic = srid == 4326,
        };

    private static MetadataV2GeometryType ToMetadataV2GeometryType(string geometryType)
    {
        var normalized = geometryType.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "point" => MetadataV2GeometryType.Point,
            "multipoint" => MetadataV2GeometryType.MultiPoint,
            "linestring" or "line" or "polyline" => MetadataV2GeometryType.LineString,
            "multilinestring" or "multiline" or "multipolyline" => MetadataV2GeometryType.MultiLineString,
            "polygon" => MetadataV2GeometryType.Polygon,
            "multipolygon" => MetadataV2GeometryType.MultiPolygon,
            "geometrycollection" => MetadataV2GeometryType.GeometryCollection,
            _ => MetadataV2GeometryType.Mixed,
        };
    }

    private static MetadataV2FieldType ToMetadataV2FieldType(string fieldType)
    {
        var normalized = fieldType.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "string" or "text" => MetadataV2FieldType.String,
            "integer" or "int" or "int32" => MetadataV2FieldType.Integer,
            "biginteger" or "long" or "bigint" or "int64" => MetadataV2FieldType.BigInteger,
            "double" or "float64" => MetadataV2FieldType.Double,
            "float" or "single" => MetadataV2FieldType.Float,
            "boolean" or "bool" => MetadataV2FieldType.Boolean,
            "datetime" or "timestamp" => MetadataV2FieldType.DateTime,
            "date" => MetadataV2FieldType.Date,
            "time" => MetadataV2FieldType.Time,
            "json" or "jsonb" => MetadataV2FieldType.Json,
            "binary" or "bytes" => MetadataV2FieldType.Binary,
            "uuid" or "guid" => MetadataV2FieldType.Uuid,
            "geometry" => MetadataV2FieldType.Geometry,
            "geography" => MetadataV2FieldType.Geography,
            _ => MetadataV2FieldType.Unknown,
        };
    }

    private async Task AssertPersistedExtentAsync(
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax,
        double tolerance)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                ST_XMin(l.extent),
                ST_YMin(l.extent),
                ST_XMax(l.extent),
                ST_YMax(l.extent),
                ST_SRID(l.extent),
                ST_XMin(s.service_extent),
                ST_YMin(s.service_extent),
                ST_XMax(s.service_extent),
                ST_YMax(s.service_extent),
                ST_SRID(s.service_extent)
            FROM honua.layers l
            INNER JOIN honua.service_layers sl
                ON sl.layer_id = l.layer_id
            INNER JOIN honua.services s
                ON s.service_name = sl.service_name
            WHERE l.layer_id = @layerId
              AND s.service_name = @serviceName;
            """;
        command.Parameters.AddWithValue("layerId", _layerId!.Value);
        command.Parameters.AddWithValue("serviceName", _serviceName);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        AssertReaderExtent(reader, 0, expectedXmin, expectedYmin, expectedXmax, expectedYmax, tolerance);
        reader.GetInt32(4).Should().Be(4326);
        AssertReaderExtent(reader, 5, expectedXmin, expectedYmin, expectedXmax, expectedYmax, tolerance);
        reader.GetInt32(9).Should().Be(4326);
    }

    private async Task AssertLayerExtentIsNullAsync(int layerId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT extent IS NULL
            FROM honua.layers
            WHERE layer_id = @layerId;
            """;
        command.Parameters.AddWithValue("layerId", layerId);

        var result = await command.ExecuteScalarAsync();
        result.Should().Be(true);
    }

    private async Task AssertPersistedLayerAndServiceExtentAsync(
        int layerId,
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax,
        double tolerance)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                ST_XMin(l.extent),
                ST_YMin(l.extent),
                ST_XMax(l.extent),
                ST_YMax(l.extent),
                ST_SRID(l.extent),
                ST_XMin(s.service_extent),
                ST_YMin(s.service_extent),
                ST_XMax(s.service_extent),
                ST_YMax(s.service_extent),
                ST_SRID(s.service_extent)
            FROM honua.layers l
            CROSS JOIN honua.services s
            WHERE l.layer_id = @layerId
              AND s.service_name = @serviceName;
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("serviceName", _serviceName);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        AssertReaderExtent(reader, 0, expectedXmin, expectedYmin, expectedXmax, expectedYmax, tolerance);
        reader.GetInt32(4).Should().Be(4326);
        AssertReaderExtent(reader, 5, expectedXmin, expectedYmin, expectedXmax, expectedYmax, tolerance);
        reader.GetInt32(9).Should().Be(4326);
    }

    private async Task AssertFeatureServerExtentAsync(
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax,
        double tolerance)
    {
        var response = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEsriExtent(
            document.RootElement.GetProperty("extent"),
            expectedXmin,
            expectedYmin,
            expectedXmax,
            expectedYmax,
            tolerance);
    }

    private async Task AssertFeatureServerQueryExtentAsync(
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax,
        double tolerance)
    {
        var response = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}/query?where=1%3D1&returnExtentOnly=true&outSR=4326&f=json");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEsriExtent(
            document.RootElement.GetProperty("extent"),
            expectedXmin,
            expectedYmin,
            expectedXmax,
            expectedYmax,
            tolerance);
    }

    private async Task AssertOgcCollectionExtentAsync(
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax,
        double tolerance)
    {
        var response = await _client.GetAsync($"/ogc/features/collections/{_layerId}");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bbox = document.RootElement
            .GetProperty("extent")
            .GetProperty("spatial")
            .GetProperty("bbox")[0];

        bbox[0].GetDouble().Should().BeApproximately(expectedXmin, tolerance);
        bbox[1].GetDouble().Should().BeApproximately(expectedYmin, tolerance);
        bbox[2].GetDouble().Should().BeApproximately(expectedXmax, tolerance);
        bbox[3].GetDouble().Should().BeApproximately(expectedYmax, tolerance);
    }

    private static void AssertReaderExtent(
        NpgsqlDataReader reader,
        int offset,
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax,
        double tolerance)
    {
        reader.GetDouble(offset).Should().BeApproximately(expectedXmin, tolerance);
        reader.GetDouble(offset + 1).Should().BeApproximately(expectedYmin, tolerance);
        reader.GetDouble(offset + 2).Should().BeApproximately(expectedXmax, tolerance);
        reader.GetDouble(offset + 3).Should().BeApproximately(expectedYmax, tolerance);
    }

    private static void AssertEsriExtent(
        JsonElement extent,
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax,
        double tolerance)
    {
        extent.GetProperty("xmin").GetDouble().Should().BeApproximately(expectedXmin, tolerance);
        extent.GetProperty("ymin").GetDouble().Should().BeApproximately(expectedYmin, tolerance);
        extent.GetProperty("xmax").GetDouble().Should().BeApproximately(expectedXmax, tolerance);
        extent.GetProperty("ymax").GetDouble().Should().BeApproximately(expectedYmax, tolerance);
        extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    private async Task<bool> LayerMetadataExistsAsync(string tableName)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM honua.layers
                WHERE table_schema = @schema AND table_name = @table
            );
            """;
        command.Parameters.AddWithValue("schema", _schema);
        command.Parameters.AddWithValue("table", tableName);

        var result = await command.ExecuteScalarAsync();
        return result is bool exists && exists;
    }

    private async Task CreatePostGisTableAsync()
    {
        var sql = $"""
            CREATE TABLE public.{_tableName} (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL,
                population INTEGER,
                geom geometry(Point, 4326) NOT NULL
            );

            INSERT INTO public.{_tableName} (name, population, geom)
            VALUES ('Test Feature', 100, ST_SetSRID(ST_Point(1, 1), 4326));
            """;

        await _fixture.Postgres.ExecuteAsync(sql);
    }

    private async Task InsertPostGisFeatureAsync(string name, int population, double x, double y)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO public.{_tableName} (name, population, geom)
            VALUES (@name, @population, ST_SetSRID(ST_Point(@x, @y), 4326));
            """;
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("population", population);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreatePostGisTableAsync(string connectionString, string tableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE TABLE public.{tableName} (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL,
                population INTEGER,
                geom geometry(Point, 4326) NOT NULL
            );
            INSERT INTO public.{tableName} (name, population, geom)
            VALUES ('Test Feature', 100, ST_SetSRID(ST_Point(1, 1), 4326));
            """;

        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateSecureConnectionAsync(string connectionString)
    {
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IConnectionEncryptionService>();

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Host) ||
            string.IsNullOrWhiteSpace(builder.Database) ||
            string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException("Connection string is missing required connection details.");
        }

        var encrypted = await encryptionService.EncryptConnectionStringAsync(connectionString);
        var keyVersion = await encryptionService.GetCurrentKeyVersionAsync();
        var sslRequired = builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull;
        var sslMode = Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true);

        _connectionName = $"layer-publish-{Guid.NewGuid():N}";

        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: _connectionName,
            host: builder.Host!,
            port: builder.Port,
            databaseName: builder.Database!,
            username: builder.Username!,
            encryptedConnectionString: encrypted,
            encryptionKeyVersion: keyVersion,
            createdBy: nameof(LayerPublishingIntegrationTests),
            description: "Layer publishing integration test connection",
            sslRequired: sslRequired,
            sslMode: sslMode);

        var created = await registry.CreateConnectionAsync(connection);
        _connectionId = created.ConnectionId;
        _connectionCreated = true;
    }

    private async Task DeleteSecureConnectionAsync()
    {
        if (!_connectionCreated)
        {
            return;
        }

        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        await registry.DeleteConnectionAsync(_connectionId);
    }

    private async Task DeleteSecureConnectionAsync(Guid connectionId)
    {
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        await registry.DeleteConnectionAsync(connectionId);
    }

    private async Task DropPostGisTableAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableName))
        {
            return;
        }

        var sql = $"DROP TABLE IF EXISTS public.{_tableName};";
        await _fixture.Postgres.ExecuteAsync(sql);
    }

    private async Task<Guid> CreateTransientSecureConnectionAsync(string connectionString, string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IConnectionEncryptionService>();

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var encrypted = await encryptionService.EncryptConnectionStringAsync(connectionString);
        var keyVersion = await encryptionService.GetCurrentKeyVersionAsync();
        var sslRequired = builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull;
        var sslMode = Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true);

        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: name,
            host: builder.Host!,
            port: builder.Port,
            databaseName: builder.Database!,
            username: builder.Username!,
            encryptedConnectionString: encrypted,
            encryptionKeyVersion: keyVersion,
            createdBy: nameof(LayerPublishingIntegrationTests),
            description: "Transient external layer publishing integration test connection",
            sslRequired: sslRequired,
            sslMode: sslMode);

        var created = await registry.CreateConnectionAsync(connection);
        return created.ConnectionId;
    }

    private string BuildConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            Database = databaseName,
            Pooling = false
        };

        return builder.ConnectionString;
    }

    private string BuildAdminConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };

        return builder.ConnectionString;
    }

    private async Task<string> CreateConnectionDatabaseAsync()
    {
        var databaseName = $"conn_{Guid.NewGuid():N}".ToLowerInvariant();
        var adminConnectionString = BuildAdminConnectionString();

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {databaseName};";
        await command.ExecuteNonQueryAsync();

        return databaseName;
    }

    private async Task DropConnectionDatabaseAsync(string databaseName)
    {
        var adminConnectionString = BuildAdminConnectionString();

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        await using (var terminateCommand = connection.CreateCommand())
        {
            terminateCommand.CommandText = """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @db_name
                  AND pid <> pg_backend_pid();
                """;
            terminateCommand.Parameters.AddWithValue("db_name", databaseName);
            await terminateCommand.ExecuteNonQueryAsync();
        }

        await using (var dropCommand = connection.CreateCommand())
        {
            dropCommand.CommandText = $"DROP DATABASE IF EXISTS {databaseName};";
            await dropCommand.ExecuteNonQueryAsync();
        }
    }

    private async Task CleanupPublishedLayerAsync()
    {
        if (_layerId is null && string.IsNullOrWhiteSpace(_serviceName))
        {
            return;
        }

        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM features
            WHERE (@hasLayerId AND layer_id = @layerId)
               OR layer_id IN (
                    SELECT layer_id
                    FROM honua.service_layers
                    WHERE service_name = @serviceName
               );

            DELETE FROM honua.layers
            WHERE (@hasLayerId AND layer_id = @layerId)
               OR layer_id IN (
                    SELECT layer_id
                    FROM honua.service_layers
                    WHERE service_name = @serviceName
               );
            """;
        command.Parameters.AddWithValue("hasLayerId", _layerId.HasValue);
        command.Parameters.AddWithValue("layerId", _layerId.GetValueOrDefault());
        command.Parameters.AddWithValue("serviceName", _serviceName);
        await command.ExecuteNonQueryAsync();

        if (!string.IsNullOrWhiteSpace(_serviceName))
        {
            await using var serviceCommand = connection.CreateCommand();
            serviceCommand.CommandText = "DELETE FROM honua.services WHERE service_name = @serviceName;";
            serviceCommand.Parameters.AddWithValue("serviceName", _serviceName);
            await serviceCommand.ExecuteNonQueryAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    public async Task ListLayers_WithInvalidConnectionId_Returns404()
    {
        var nonExistentConnectionId = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/v1/admin/connections/{nonExistentConnectionId}/layers");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_WithMissingRequiredFields_Returns400()
    {
        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = "", // Missing table
            LayerName = "",
            GeometryColumn = "",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = Array.Empty<string>(),
            ServiceName = _serviceName,
            Enabled = true
        };

        var response = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_DuplicateLayer_ReturnsConflictOrBadRequest()
    {
        var publishRequest = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            Description = "Layer publish duplicate test",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = new[] { "id", "name", "population" },
            ServiceName = _serviceName,
            Enabled = true
        };

        var firstResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        var firstPayload = await firstResponse.Content.ReadAsStringAsync();
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created, $"first publish response: {firstPayload}");
        var firstApi = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(firstPayload, _jsonOptions);
        _layerId = firstApi?.Data?.LayerId;

        // Second publish of same table should fail
        var secondResponse = await _client.PostAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(publishRequest, options: _jsonOptions));

        secondResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/{layerId}/enabled")]
    public async Task SetLayerEnabled_NonexistentLayer_Returns404()
    {
        var toggleRequest = new LayerEnabledRequest { Enabled = false };
        var nonExistentLayerId = 999999;

        var response = await _client.PutAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers/{nonExistentLayerId}/enabled?serviceName={_serviceName}",
            JsonContent.Create(toggleRequest, options: _jsonOptions));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_WhenApprovalRequired_ReturnsForbidden()
    {
        var approvalFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator>();
                services.AddSingleton<Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator>(
                    new AlwaysRequiresApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var client = approvalFixture.CreateAdminClient();

            var publishRequest = new PublishLayerRequest
            {
                Schema = _schema,
                Table = _tableName,
                LayerName = $"Layer approval test",
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = new[] { "id", "name", "population" },
                ServiceName = _serviceName,
                Enabled = true
            };

            var response = await client.PostAsync(
                $"/api/v1/admin/connections/{_connectionId}/layers",
                JsonContent.Create(publishRequest, options: _jsonOptions));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    private sealed class AlwaysRequiresApprovalEvaluator : Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator
    {
        public Core.Features.Authorization.Domain.ApprovalRequirement Evaluate(
            System.Security.Claims.ClaimsPrincipal principal,
            Core.Features.Authorization.Domain.OperatorAuthorizationRequest request)
            => Core.Features.Authorization.Domain.ApprovalRequirement.Required(
                "operator.test-policy", "test-approval-required");
    }

    private async Task ResetPublishedMetadataAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM honua.layer_fields;
            DELETE FROM honua.service_layers;
            DELETE FROM honua.layers;
            DELETE FROM honua.services;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
