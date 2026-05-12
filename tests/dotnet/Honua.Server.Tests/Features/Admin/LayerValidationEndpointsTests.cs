// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin layer storage validation endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class LayerValidationEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;
    private string _schema = string.Empty;
    private string _tableName = string.Empty;
    private string _serviceName = string.Empty;
    private int? _layerId;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
        _schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Test schema was not initialized.");
        _tableName = $"layer_validation_{Guid.NewGuid():N}";
        _serviceName = $"svc_validation_{Guid.NewGuid():N}";

        await CreateSpatialTableAsync();
        await CreateLayerMetadataAsync();
    }

    public async Task DisposeAsync()
    {
        await CleanupLayerMetadataAsync();
        await DropSpatialTableAsync();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/validation")]
    public async Task GetLayerValidation_WithMatchingStorage_ReturnsValid()
    {
        var response = await _client.GetAsync($"/api/v1/admin/metadata/layers/{_layerId}/validation");

        response.Be200Ok();
        var apiResponse = await ReadValidationResponseAsync(response);

        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.IsValid.Should().BeTrue();
        apiResponse.Data.Status.Should().Be("valid");
        apiResponse.Data.StorageSchema.Should().Be(_schema);
        apiResponse.Data.StorageTable.Should().Be(_tableName);
        apiResponse.Data.Checks.Should().NotContain(check => check.Severity == "error");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/validation")]
    public async Task GetLayerValidation_WhenDeclaredColumnIsDropped_ReturnsInvalid()
    {
        await DropPopulationColumnAsync();

        var response = await _client.GetAsync($"/api/v1/admin/metadata/layers/{_layerId}/validation");

        response.Be200Ok();
        var apiResponse = await ReadValidationResponseAsync(response);

        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.IsValid.Should().BeFalse();
        apiResponse.Data.Status.Should().Be("invalid");
        apiResponse.Data.Checks.Should().Contain(check =>
            check.Code == "declared-field" &&
            check.Severity == "error" &&
            check.Expected == "population" &&
            check.Actual == null);
    }

    private static async Task<ApiResponse<LayerValidationResponse>> ReadValidationResponseAsync(
        HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            LayerValidationJsonContext.Default.ApiResponseLayerValidationResponse);

        apiResponse.Should().NotBeNull($"response payload was: {payload}");
        return apiResponse!;
    }

    private async Task CreateSpatialTableAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {QuoteIdentifier(_tableName)} (
                id integer PRIMARY KEY,
                name text NOT NULL,
                population integer,
                geom geometry(Point, 4326) NOT NULL
            );

            INSERT INTO {QuoteIdentifier(_tableName)} (id, name, population, geom)
            VALUES
                (1, 'Feature One', 100, ST_SetSRID(ST_Point(-157.8583, 21.3069), 4326)),
                (2, 'Feature Two', 250, ST_SetSRID(ST_Point(-157.8167, 21.2970), 4326));
            """;

        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateLayerMetadataAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);

        await using (var sequenceCommand = connection.CreateCommand())
        {
            sequenceCommand.CommandText = """
                SELECT setval(
                    pg_get_serial_sequence('honua.layers', 'layer_id'),
                    GREATEST((SELECT COALESCE(MAX(layer_id), 1) FROM honua.layers), 1),
                    true);
                """;
            await sequenceCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.services (
                service_name,
                description,
                srid,
                supported_formats,
                capabilities,
                service_extent
            )
            VALUES (
                @serviceName,
                'Layer validation endpoint test service',
                4326,
                ARRAY['JSON', 'GeoJSON'],
                ARRAY['Query'],
                ST_MakeEnvelope(-158.0, 21.0, -157.0, 22.0, 4326)
            );

            INSERT INTO honua.layers (
                layer_name,
                description,
                table_schema,
                table_name,
                primary_key_column,
                geometry_column,
                storage_srid,
                geometry_type,
                srid,
                extent,
                default_visibility,
                enabled
            )
            VALUES (
                @layerName,
                'Layer validation endpoint test layer',
                @schema,
                @tableName,
                'id',
                'geom',
                4326,
                'Point',
                4326,
                ST_MakeEnvelope(-158.0, 21.0, -157.0, 22.0, 4326),
                true,
                true
            )
            RETURNING layer_id;
            """;
        command.Parameters.AddWithValue("serviceName", _serviceName);
        command.Parameters.AddWithValue("layerName", $"Layer {_tableName}");
        command.Parameters.AddWithValue("schema", _schema);
        command.Parameters.AddWithValue("tableName", _tableName);

        var layerId = await command.ExecuteScalarAsync();
        _layerId = Convert.ToInt32(layerId, CultureInfo.InvariantCulture);

        await using var fieldsCommand = connection.CreateCommand();
        fieldsCommand.CommandText = """
            INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
            VALUES (@serviceName, @layerId, 0);

            INSERT INTO honua.layer_fields (
                layer_id,
                field_name,
                field_type,
                field_order,
                max_length,
                nullable,
                description
            )
            VALUES
                (@layerId, 'id', 'Integer', 0, null, false, 'Identifier'),
                (@layerId, 'name', 'String', 1, null, false, 'Name'),
                (@layerId, 'population', 'Integer', 2, null, true, 'Population'),
                (@layerId, 'geom', 'Geometry', 3, null, false, 'Geometry');
            """;
        fieldsCommand.Parameters.AddWithValue("serviceName", _serviceName);
        fieldsCommand.Parameters.AddWithValue("layerId", _layerId.Value);

        await fieldsCommand.ExecuteNonQueryAsync();
    }

    private async Task DropPopulationColumnAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {QuoteIdentifier(_tableName)} DROP COLUMN population;";

        await command.ExecuteNonQueryAsync();
    }

    private async Task CleanupLayerMetadataAsync()
    {
        if (_layerId.HasValue)
        {
            await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM honua.layer_fields WHERE layer_id = @layerId;
                DELETE FROM honua.service_layers WHERE layer_id = @layerId;
                DELETE FROM honua.layers WHERE layer_id = @layerId;
                """;
            command.Parameters.AddWithValue("layerId", _layerId.Value);
            await command.ExecuteNonQueryAsync();
        }

        if (!string.IsNullOrWhiteSpace(_serviceName))
        {
            await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM honua.services WHERE service_name = @serviceName;";
            command.Parameters.AddWithValue("serviceName", _serviceName);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task DropSpatialTableAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableName))
        {
            return;
        }

        await using var connection = await _fixture.Postgres.GetConnectionAsync(_schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(_tableName)};";

        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (identifier.Any(static ch => !char.IsLetterOrDigit(ch) && ch != '_'))
        {
            throw new ArgumentException($"Invalid identifier '{identifier}'.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
