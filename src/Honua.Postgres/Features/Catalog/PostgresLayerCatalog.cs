// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;

namespace Honua.Postgres.Features.Catalog;

/// <summary>
/// PostgreSQL implementation of layer catalog for PostGIS metadata discovery
/// </summary>
internal sealed class PostgresLayerCatalog : ILayerCatalog
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _layersTable;
    private readonly string _fieldsTable;
    private readonly string _servicesTable;
    private readonly string _serviceLayersTable;

    public PostgresLayerCatalog(NpgsqlDataSource dataSource, string? schemaName = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        var schema = string.IsNullOrEmpty(schemaName) ? "catalog" : schemaName;
        _layersTable = $"{schema}.layers";
        _fieldsTable = $"{schema}.layer_fields";
        _servicesTable = $"{schema}.services";
        _serviceLayersTable = $"{schema}.service_layers";
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.geometry_type,
                l.srid,
                l.min_scale,
                l.max_scale,
                l.default_visibility,
                ST_XMin(l.extent) as xmin,
                ST_YMin(l.extent) as ymin,
                ST_XMax(l.extent) as xmax,
                ST_YMax(l.extent) as ymax
            FROM {_layersTable} l
            WHERE l.layer_id = @layerId
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var layer = ReadLayerDefinition(reader);
        reader.Close();

        // Get field definitions for this layer
        var fields = await GetLayerFieldsAsync(layerId, cancellationToken);

        return layer with { Fields = fields };
    }

    /// <inheritdoc />
    public async Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.geometry_type,
                l.srid,
                l.min_scale,
                l.max_scale,
                l.default_visibility,
                ST_XMin(l.extent) as xmin,
                ST_YMin(l.extent) as ymin,
                ST_XMax(l.extent) as xmax,
                ST_YMax(l.extent) as ymax
            FROM {_layersTable} l
            ORDER BY l.layer_id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var layers = new List<LayerDefinition>();
        var layerIds = new List<int>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var layer = ReadLayerDefinition(reader);
            layers.Add(layer);
            layerIds.Add(layer.Id);
        }
        reader.Close();

        // Get fields for all layers in batch
        var fieldsMap = await GetLayerFieldsBatchAsync(layerIds.ToArray(), cancellationToken);

        // Combine layers with their fields
        return layers.Select(layer => layer with
        {
            Fields = fieldsMap.TryGetValue(layer.Id, out var fields) ? fields : []
        }).ToArray();
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT
                s.service_name,
                s.description,
                s.srid,
                s.max_record_count,
                s.supported_formats,
                s.capabilities,
                ST_XMin(s.service_extent) as xmin,
                ST_YMin(s.service_extent) as ymin,
                ST_XMax(s.service_extent) as xmax,
                ST_YMax(s.service_extent) as ymax
            FROM {_servicesTable} s
            WHERE LOWER(s.service_name) = LOWER(@serviceName)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", serviceName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var service = ReadServiceDefinition(reader);
        reader.Close();

        // Get layers for this service
        var layers = await GetServiceLayersAsync(serviceName, cancellationToken);

        return service with { Layers = layers };
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT
                s.service_name,
                s.description,
                s.srid,
                s.max_record_count,
                s.supported_formats,
                s.capabilities,
                ST_XMin(s.service_extent) as xmin,
                ST_YMin(s.service_extent) as ymin,
                ST_XMax(s.service_extent) as xmax,
                ST_YMax(s.service_extent) as ymax
            FROM {_servicesTable} s
            ORDER BY s.service_name
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var services = new List<ServiceDefinition>();
        var serviceNames = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var service = ReadServiceDefinition(reader);
            services.Add(service);
            serviceNames.Add(service.Name);
        }
        reader.Close();

        // Get layers for all services in batch
        var layersMap = await GetServiceLayersBatchAsync(serviceNames.ToArray(), cancellationToken);

        // Combine services with their layers
        return services.Select(service => service with
        {
            Layers = layersMap.TryGetValue(service.Name, out var layers) ? layers : []
        }).ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT 1 FROM {_layersTable} WHERE layer_id = @layerId";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@layerId", layerId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    /// <inheritdoc />
    public async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT 1 FROM {_servicesTable} WHERE LOWER(service_name) = LOWER(@serviceName)";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", serviceName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    private static LayerDefinition ReadLayerDefinition(NpgsqlDataReader reader)
    {
        var id = reader.GetInt32(reader.GetOrdinal("layer_id"));
        var name = reader.GetString(reader.GetOrdinal("layer_name"));
        var description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description"));
        var geometryType = Enum.Parse<GeometryType>(reader.GetString(reader.GetOrdinal("geometry_type")));
        var srid = reader.GetInt32(reader.GetOrdinal("srid"));
        var minScale = reader.IsDBNull(reader.GetOrdinal("min_scale")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("min_scale"));
        var maxScale = reader.IsDBNull(reader.GetOrdinal("max_scale")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("max_scale"));
        var defaultVisibility = reader.GetBoolean(reader.GetOrdinal("default_visibility"));

        // Build extent if available
        FeatureExtent? extent = null;
        var xminOrdinal = reader.GetOrdinal("xmin");
        if (!reader.IsDBNull(xminOrdinal))
        {
            var xmin = reader.GetDouble(xminOrdinal);
            var ymin = reader.GetDouble(reader.GetOrdinal("ymin"));
            var xmax = reader.GetDouble(reader.GetOrdinal("xmax"));
            var ymax = reader.GetDouble(reader.GetOrdinal("ymax"));
            extent = FeatureExtent.Create(xmin, ymin, xmax, ymax, srid);
        }

        var spatialReference = new SpatialReference(srid);

        return new LayerDefinition(
            id,
            name,
            description,
            geometryType,
            spatialReference,
            [], // Fields populated separately
            extent,
            minScale,
            maxScale,
            defaultVisibility);
    }

    private static ServiceDefinition ReadServiceDefinition(NpgsqlDataReader reader)
    {
        var name = reader.GetString(reader.GetOrdinal("service_name"));
        var description = reader.GetString(reader.GetOrdinal("description"));
        var srid = reader.GetInt32(reader.GetOrdinal("srid"));
        var maxRecordCount = reader.GetInt32(reader.GetOrdinal("max_record_count"));
        var supportedFormats = reader.GetFieldValue<string[]>(reader.GetOrdinal("supported_formats"));
        var capabilities = reader.GetFieldValue<string[]>(reader.GetOrdinal("capabilities"));

        // Build service extent if available
        FeatureExtent? extent = null;
        var xminOrdinal = reader.GetOrdinal("xmin");
        if (!reader.IsDBNull(xminOrdinal))
        {
            var xmin = reader.GetDouble(xminOrdinal);
            var ymin = reader.GetDouble(reader.GetOrdinal("ymin"));
            var xmax = reader.GetDouble(reader.GetOrdinal("xmax"));
            var ymax = reader.GetDouble(reader.GetOrdinal("ymax"));
            extent = FeatureExtent.Create(xmin, ymin, xmax, ymax, srid);
        }

        var spatialReference = new SpatialReference(srid);

        return new ServiceDefinition(
            name,
            description,
            [], // Layers populated separately
            spatialReference,
            maxRecordCount,
            supportedFormats,
            capabilities,
            extent);
    }

    private async Task<FieldDefinition[]> GetLayerFieldsAsync(int layerId, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                f.field_name,
                f.field_type,
                f.max_length,
                f.nullable,
                f.default_value,
                f.description
            FROM {_fieldsTable} f
            WHERE f.layer_id = @layerId
            ORDER BY f.field_order
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fields = new List<FieldDefinition>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var fieldName = reader.GetString(reader.GetOrdinal("field_name"));
            var fieldType = Enum.Parse<FieldType>(reader.GetString(reader.GetOrdinal("field_type")));
            var maxLengthOrdinal = reader.GetOrdinal("max_length");
            var maxLength = reader.IsDBNull(maxLengthOrdinal) ? null : (int?)reader.GetInt32(maxLengthOrdinal);
            var nullable = reader.GetBoolean(reader.GetOrdinal("nullable"));
            var defaultValueOrdinal = reader.GetOrdinal("default_value");
            var defaultValue = reader.IsDBNull(defaultValueOrdinal) ? null : reader.GetValue(defaultValueOrdinal);
            var descriptionOrdinal = reader.GetOrdinal("description");
            var description = reader.IsDBNull(descriptionOrdinal) ? null : reader.GetString(descriptionOrdinal);

            fields.Add(new FieldDefinition(fieldName, fieldType, maxLength, nullable, defaultValue, description));
        }

        return fields.ToArray();
    }

    private async Task<Dictionary<int, FieldDefinition[]>> GetLayerFieldsBatchAsync(int[] layerIds, CancellationToken cancellationToken)
    {
        if (layerIds.Length == 0)
            return new Dictionary<int, FieldDefinition[]>();

        var sql = $"""
            SELECT
                f.layer_id,
                f.field_name,
                f.field_type,
                f.max_length,
                f.nullable,
                f.default_value,
                f.description
            FROM {_fieldsTable} f
            WHERE f.layer_id = ANY(@layerIds)
            ORDER BY f.layer_id, f.field_order
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@layerIds", layerIds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fieldsMap = new Dictionary<int, List<FieldDefinition>>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var layerId = reader.GetInt32(reader.GetOrdinal("layer_id"));
            var fieldName = reader.GetString(reader.GetOrdinal("field_name"));
            var fieldType = Enum.Parse<FieldType>(reader.GetString(reader.GetOrdinal("field_type")));
            var maxLengthOrdinal = reader.GetOrdinal("max_length");
            var maxLength = reader.IsDBNull(maxLengthOrdinal) ? null : (int?)reader.GetInt32(maxLengthOrdinal);
            var nullable = reader.GetBoolean(reader.GetOrdinal("nullable"));
            var defaultValueOrdinal = reader.GetOrdinal("default_value");
            var defaultValue = reader.IsDBNull(defaultValueOrdinal) ? null : reader.GetValue(defaultValueOrdinal);
            var descriptionOrdinal = reader.GetOrdinal("description");
            var description = reader.IsDBNull(descriptionOrdinal) ? null : reader.GetString(descriptionOrdinal);

            if (!fieldsMap.TryGetValue(layerId, out var fieldsList))
            {
                fieldsList = new List<FieldDefinition>();
                fieldsMap[layerId] = fieldsList;
            }

            fieldsList.Add(new FieldDefinition(fieldName, fieldType, maxLength, nullable, defaultValue, description));
        }

        return fieldsMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    private async Task<LayerDefinition[]> GetServiceLayersAsync(string serviceName, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT l.layer_id
            FROM {_serviceLayersTable} sl
            JOIN {_layersTable} l ON sl.layer_id = l.layer_id
            WHERE LOWER(sl.service_name) = LOWER(@serviceName)
            ORDER BY sl.layer_order, l.layer_id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", serviceName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var layerIds = new List<int>();

        while (await reader.ReadAsync(cancellationToken))
        {
            layerIds.Add(reader.GetInt32(reader.GetOrdinal("layer_id")));
        }

        // Get full layer definitions for these IDs
        var layers = new List<LayerDefinition>();
        foreach (var layerId in layerIds)
        {
            var layer = await GetLayerAsync(layerId, cancellationToken);
            if (layer != null)
                layers.Add(layer);
        }

        return layers.ToArray();
    }

    private async Task<Dictionary<string, LayerDefinition[]>> GetServiceLayersBatchAsync(string[] serviceNames, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LayerDefinition[]>();

        foreach (var serviceName in serviceNames)
        {
            var layers = await GetServiceLayersAsync(serviceName, cancellationToken);
            result[serviceName] = layers;
        }

        return result;
    }
}
