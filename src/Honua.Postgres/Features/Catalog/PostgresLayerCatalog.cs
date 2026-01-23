// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Npgsql;

namespace Honua.Postgres.Features.Catalog;

/// <summary>
/// PostgreSQL implementation of layer catalog for PostGIS metadata discovery
/// </summary>
internal sealed class PostgresLayerCatalog : ILayerCatalog
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _layersTable;
    private readonly string _fieldsTable;
    private readonly string _servicesTable;
    private readonly string _serviceLayersTable;
    private readonly string _relationshipsTable;

    public PostgresLayerCatalog(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

        string schema = string.IsNullOrEmpty(schemaName) ? "honua" : schemaName;
        _layersTable = $"{schema}.layers";
        _fieldsTable = $"{schema}.layer_fields";
        _servicesTable = $"{schema}.services";
        _serviceLayersTable = $"{schema}.service_layers";
        _relationshipsTable = $"{schema}.relationships";
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.geometry_type,
                l.srid,
                l.min_scale,
                l.max_scale,
                l.default_visibility,
                l.metadata,
                ST_XMin(l.extent) as xmin,
                ST_YMin(l.extent) as ymin,
                ST_XMax(l.extent) as xmax,
                ST_YMax(l.extent) as ymax,
                ST_SRID(l.extent) as extent_srid
            FROM {_layersTable} l
            WHERE l.layer_id = @layerId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        LayerDefinition layer = ReadLayerDefinition(reader);
        reader.Close();

        // Get field definitions for this layer
        FieldDefinition[] fields = await GetLayerFieldsAsync(layerId, cancellationToken);

        // Get relationships for this layer
        Relationship[] relationships = await ListRelationshipsAsync(layerId, cancellationToken);

        return layer with { Fields = fields, Relationships = relationships };
    }

    /// <inheritdoc />
    public async Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.geometry_type,
                l.srid,
                l.min_scale,
                l.max_scale,
                l.default_visibility,
                l.metadata,
                ST_XMin(l.extent) as xmin,
                ST_YMin(l.extent) as ymin,
                ST_XMax(l.extent) as xmax,
                ST_YMax(l.extent) as ymax,
                ST_SRID(l.extent) as extent_srid
            FROM {_layersTable} l
            ORDER BY l.layer_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        var layers = new List<LayerDefinition>();
        var layerIds = new List<int>();

        while (await reader.ReadAsync(cancellationToken))
        {
            LayerDefinition layer = ReadLayerDefinition(reader);
            layers.Add(layer);
            layerIds.Add(layer.Id);
        }
        reader.Close();

        // Get fields for all layers in batch
        Dictionary<int, FieldDefinition[]> fieldsMap = await GetLayerFieldsBatchAsync([.. layerIds], cancellationToken);

        // Get relationships for all layers in batch
        Dictionary<int, Relationship[]> relationshipsMap = await GetLayerRelationshipsBatchAsync([.. layerIds], cancellationToken);

        // Combine layers with their fields and relationships
        return [.. layers.Select(layer => layer with
        {
            Fields = fieldsMap.TryGetValue(layer.Id, out FieldDefinition[]? fields) ? fields : [],
            Relationships = relationshipsMap.TryGetValue(layer.Id, out Relationship[]? relationships) ? relationships : []
        })];
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT
                s.service_name,
                s.description,
                s.srid,
                s.supported_formats,
                s.capabilities,
                s.metadata,
                s.connection_id,
                ST_XMin(s.service_extent) as xmin,
                ST_YMin(s.service_extent) as ymin,
                ST_XMax(s.service_extent) as xmax,
                ST_YMax(s.service_extent) as ymax,
                ST_SRID(s.service_extent) as extent_srid
            FROM {_servicesTable} s
            WHERE LOWER(s.service_name) = LOWER(@serviceName)
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@serviceName", serviceName);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        ServiceDefinition service = ReadServiceDefinition(reader);
        reader.Close();

        // Get layers for this service
        LayerDefinition[] layers = await GetServiceLayersAsync(serviceName, cancellationToken);

        return service with { Layers = layers };
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT
                s.service_name,
                s.description,
                s.srid,
                s.supported_formats,
                s.capabilities,
                s.metadata,
                s.connection_id,
                ST_XMin(s.service_extent) as xmin,
                ST_YMin(s.service_extent) as ymin,
                ST_XMax(s.service_extent) as xmax,
                ST_YMax(s.service_extent) as ymax,
                ST_SRID(s.service_extent) as extent_srid
            FROM {_servicesTable} s
            ORDER BY s.service_name
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        var services = new List<ServiceDefinition>();
        var serviceNames = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            ServiceDefinition service = ReadServiceDefinition(reader);
            services.Add(service);
            serviceNames.Add(service.Name);
        }
        reader.Close();

        // Get layers for all services in batch
        Dictionary<string, LayerDefinition[]> layersMap = await GetServiceLayersBatchAsync([.. serviceNames], cancellationToken);

        // Combine services with their layers
        return [.. services.Select(service => service with
        {
            Layers = layersMap.TryGetValue(service.Name, out LayerDefinition[]? layers) ? layers : []
        })];
    }

    /// <inheritdoc />
    public async Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string sql = $"SELECT 1 FROM {_layersTable} WHERE layer_id = @layerId";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    /// <inheritdoc />
    public async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string sql = $"SELECT 1 FROM {_servicesTable} WHERE LOWER(service_name) = LOWER(@serviceName)";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@serviceName", serviceName);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    private static LayerDefinition ReadLayerDefinition(NpgsqlDataReader reader)
    {
        int id = reader.GetInt32(reader.GetOrdinal("layer_id"));
        string name = reader.GetString(reader.GetOrdinal("layer_name"));
        string? description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description"));

        string geometryTypeString = reader.GetString(reader.GetOrdinal("geometry_type"));
        if (!Enum.TryParse(geometryTypeString, ignoreCase: true, out GeometryType geometryType))
            throw new InvalidDataException($"Invalid geometry type: {geometryTypeString}");

        int srid = reader.GetInt32(reader.GetOrdinal("srid"));
        int extentSrid = srid;
        int extentSridOrdinal = reader.GetOrdinal("extent_srid");
        if (!reader.IsDBNull(extentSridOrdinal))
        {
            extentSrid = reader.GetInt32(extentSridOrdinal);
            if (extentSrid <= 0)
            {
                extentSrid = srid;
            }
        }
        double? minScale = reader.IsDBNull(reader.GetOrdinal("min_scale")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("min_scale"));
        double? maxScale = reader.IsDBNull(reader.GetOrdinal("max_scale")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("max_scale"));
        bool defaultVisibility = reader.GetBoolean(reader.GetOrdinal("default_visibility"));

        // Build extent if available
        FeatureExtent? extent = null;
        int xminOrdinal = reader.GetOrdinal("xmin");
        if (!reader.IsDBNull(xminOrdinal))
        {
            double xmin = reader.GetDouble(xminOrdinal);
            double ymin = reader.GetDouble(reader.GetOrdinal("ymin"));
            double xmax = reader.GetDouble(reader.GetOrdinal("xmax"));
            double ymax = reader.GetDouble(reader.GetOrdinal("ymax"));
            extent = FeatureExtent.Create(xmin, ymin, xmax, ymax, extentSrid);
        }

        var spatialReference = SpatialReference.Create(srid);

        var metadata = ReadMetadata(reader, "metadata");

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
            defaultVisibility,
            Metadata: metadata);
    }

    private static ServiceDefinition ReadServiceDefinition(NpgsqlDataReader reader)
    {
        string name = reader.GetString(reader.GetOrdinal("service_name"));
        string description = reader.GetString(reader.GetOrdinal("description"));
        int srid = reader.GetInt32(reader.GetOrdinal("srid"));
        Guid? connectionId = reader.IsDBNull(reader.GetOrdinal("connection_id"))
            ? null
            : reader.GetGuid(reader.GetOrdinal("connection_id"));
        int extentSrid = srid;
        int extentSridOrdinal = reader.GetOrdinal("extent_srid");
        if (!reader.IsDBNull(extentSridOrdinal))
        {
            extentSrid = reader.GetInt32(extentSridOrdinal);
            if (extentSrid <= 0)
            {
                extentSrid = srid;
            }
        }
        string[] supportedFormats = reader.GetFieldValue<string[]>(reader.GetOrdinal("supported_formats"));
        string[] capabilities = reader.GetFieldValue<string[]>(reader.GetOrdinal("capabilities"));

        // Build service extent if available
        FeatureExtent? extent = null;
        int xminOrdinal = reader.GetOrdinal("xmin");
        if (!reader.IsDBNull(xminOrdinal))
        {
            double xmin = reader.GetDouble(xminOrdinal);
            double ymin = reader.GetDouble(reader.GetOrdinal("ymin"));
            double xmax = reader.GetDouble(reader.GetOrdinal("xmax"));
            double ymax = reader.GetDouble(reader.GetOrdinal("ymax"));
            extent = FeatureExtent.Create(xmin, ymin, xmax, ymax, extentSrid);
        }

        var spatialReference = SpatialReference.Create(srid);

        var metadata = ReadMetadata(reader, "metadata");

        return new ServiceDefinition(
            name,
            description,
            [], // Layers populated separately
            spatialReference,
            supportedFormats,
            capabilities,
            extent,
            Metadata: metadata,
            ConnectionId: connectionId);
    }

    private async Task<FieldDefinition[]> GetLayerFieldsAsync(int layerId, CancellationToken cancellationToken)
    {
        string sql = $"""
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

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var fields = new List<FieldDefinition>();

        while (await reader.ReadAsync(cancellationToken))
        {
            string fieldName = reader.GetString(reader.GetOrdinal("field_name"));

            string fieldTypeString = reader.GetString(reader.GetOrdinal("field_type"));
            if (!Enum.TryParse<FieldType>(fieldTypeString, out FieldType fieldType))
                throw new InvalidDataException($"Invalid field type: {fieldTypeString}");

            int maxLengthOrdinal = reader.GetOrdinal("max_length");
            int? maxLength = reader.IsDBNull(maxLengthOrdinal) ? null : (int?)reader.GetInt32(maxLengthOrdinal);
            bool nullable = reader.GetBoolean(reader.GetOrdinal("nullable"));
            int defaultValueOrdinal = reader.GetOrdinal("default_value");
            object? defaultValue = reader.IsDBNull(defaultValueOrdinal) ? null : reader.GetValue(defaultValueOrdinal);
            int descriptionOrdinal = reader.GetOrdinal("description");
            string? description = reader.IsDBNull(descriptionOrdinal) ? null : reader.GetString(descriptionOrdinal);

            fields.Add(new FieldDefinition(fieldName, fieldType, maxLength, nullable, defaultValue, description));
        }

        return [.. fields];
    }

    private async Task<Dictionary<int, FieldDefinition[]>> GetLayerFieldsBatchAsync(int[] layerIds, CancellationToken cancellationToken)
    {
        if (layerIds.Length == 0)
            return [];

        string sql = $"""
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

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerIds", layerIds);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var fieldsMap = new Dictionary<int, List<FieldDefinition>>();

        while (await reader.ReadAsync(cancellationToken))
        {
            int layerId = reader.GetInt32(reader.GetOrdinal("layer_id"));
            string fieldName = reader.GetString(reader.GetOrdinal("field_name"));

            string fieldTypeString = reader.GetString(reader.GetOrdinal("field_type"));
            if (!Enum.TryParse<FieldType>(fieldTypeString, out FieldType fieldType))
                throw new InvalidDataException($"Invalid field type: {fieldTypeString}");

            int maxLengthOrdinal = reader.GetOrdinal("max_length");
            int? maxLength = reader.IsDBNull(maxLengthOrdinal) ? null : (int?)reader.GetInt32(maxLengthOrdinal);
            bool nullable = reader.GetBoolean(reader.GetOrdinal("nullable"));
            int defaultValueOrdinal = reader.GetOrdinal("default_value");
            object? defaultValue = reader.IsDBNull(defaultValueOrdinal) ? null : reader.GetValue(defaultValueOrdinal);
            int descriptionOrdinal = reader.GetOrdinal("description");
            string? description = reader.IsDBNull(descriptionOrdinal) ? null : reader.GetString(descriptionOrdinal);

            if (!fieldsMap.TryGetValue(layerId, out List<FieldDefinition>? fieldsList))
            {
                fieldsList = [];
                fieldsMap[layerId] = fieldsList;
            }

            fieldsList.Add(new FieldDefinition(fieldName, fieldType, maxLength, nullable, defaultValue, description));
        }

        return fieldsMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    private async Task<LayerDefinition[]> GetServiceLayersAsync(string serviceName, CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT l.layer_id
            FROM {_serviceLayersTable} sl
            JOIN {_layersTable} l ON sl.layer_id = l.layer_id
            WHERE LOWER(sl.service_name) = LOWER(@serviceName)
            ORDER BY sl.layer_order, l.layer_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@serviceName", serviceName);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var layerIds = new List<int>();

        while (await reader.ReadAsync(cancellationToken))
        {
            layerIds.Add(reader.GetInt32(reader.GetOrdinal("layer_id")));
        }

        // Get full layer definitions for these IDs
        var layers = new List<LayerDefinition>();
        foreach (int layerId in layerIds)
        {
            LayerDefinition? layer = await GetLayerAsync(layerId, cancellationToken);
            if (layer != null)
                layers.Add(layer);
        }

        return [.. layers];
    }

    private async Task<Dictionary<string, LayerDefinition[]>> GetServiceLayersBatchAsync(string[] serviceNames, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LayerDefinition[]>();

        foreach (string serviceName in serviceNames)
        {
            LayerDefinition[] layers = await GetServiceLayersAsync(serviceName, cancellationToken);
            result[serviceName] = layers;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT
                r.relationship_id,
                r.name,
                r.related_layer_id,
                r.relationship_type,
                r.origin_foreign_key,
                r.destination_foreign_key,
                r.description
            FROM {_relationshipsTable} r
            WHERE r.layer_id = @layerId AND r.relationship_id = @relationshipId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.AddWithValue("@relationshipId", relationshipId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return !await reader.ReadAsync(cancellationToken) ? null : ReadRelationship(reader);
    }

    /// <inheritdoc />
    public async Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT
                r.relationship_id,
                r.name,
                r.related_layer_id,
                r.relationship_type,
                r.origin_foreign_key,
                r.destination_foreign_key,
                r.description
            FROM {_relationshipsTable} r
            WHERE r.layer_id = @layerId
            ORDER BY r.relationship_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var relationships = new List<Relationship>();

        while (await reader.ReadAsync(cancellationToken))
        {
            relationships.Add(ReadRelationship(reader));
        }

        return [.. relationships];
    }

    private async Task<Dictionary<int, Relationship[]>> GetLayerRelationshipsBatchAsync(int[] layerIds, CancellationToken cancellationToken)
    {
        if (layerIds.Length == 0)
            return [];

        string sql = $"""
            SELECT
                r.layer_id,
                r.relationship_id,
                r.name,
                r.related_layer_id,
                r.relationship_type,
                r.origin_foreign_key,
                r.destination_foreign_key,
                r.description
            FROM {_relationshipsTable} r
            WHERE r.layer_id = ANY(@layerIds)
            ORDER BY r.layer_id, r.relationship_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerIds", layerIds);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var relationshipsMap = new Dictionary<int, List<Relationship>>();

        while (await reader.ReadAsync(cancellationToken))
        {
            int layerId = reader.GetInt32(reader.GetOrdinal("layer_id"));
            Relationship relationship = ReadRelationship(reader, layerId);

            if (!relationshipsMap.TryGetValue(layerId, out List<Relationship>? relationshipsList))
            {
                relationshipsList = [];
                relationshipsMap[layerId] = relationshipsList;
            }

            relationshipsList.Add(relationship);
        }

        return relationshipsMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    private static Relationship ReadRelationship(NpgsqlDataReader reader, int? layerId = null)
    {
        int relationshipId = reader.GetInt32(reader.GetOrdinal("relationship_id"));
        string name = reader.GetString(reader.GetOrdinal("name"));
        int relatedLayerId = reader.GetInt32(reader.GetOrdinal("related_layer_id"));
        string relationshipType = reader.GetString(reader.GetOrdinal("relationship_type"));
        string originForeignKey = reader.GetString(reader.GetOrdinal("origin_foreign_key"));
        string destinationForeignKey = reader.GetString(reader.GetOrdinal("destination_foreign_key"));
        int descriptionOrdinal = reader.GetOrdinal("description");
        string? description = reader.IsDBNull(descriptionOrdinal) ? null : reader.GetString(descriptionOrdinal);

        return Relationship.Create(
            relationshipId,
            name,
            relatedLayerId,
            relationshipType,
            originForeignKey,
            destinationForeignKey,
            description);
    }

    private static CatalogMetadata? ReadMetadata(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogMetadata);
    }
}
