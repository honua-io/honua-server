// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Integration tests for layer publishing admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
public sealed class LayerPublishingIntegrationTests : IAsyncLifetime
{
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

        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"layer-publish-{Guid.NewGuid():N}",
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
        if (_layerId is null)
        {
            return;
        }

        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM honua.layer_fields WHERE layer_id = @layerId;
            DELETE FROM honua.service_layers WHERE layer_id = @layerId;
            DELETE FROM honua.layers WHERE layer_id = @layerId;
            """;
        command.Parameters.AddWithValue("layerId", _layerId.Value);
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
