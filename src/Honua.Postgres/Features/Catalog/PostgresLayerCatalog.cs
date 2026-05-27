// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Validation;
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
        // Validation framework eliminates 1 line of duplicate null check
        _connectionProvider = connectionProvider.ThrowIfNull();

        _layersTable = Infrastructure.SchemaSearchPath.QualifyTable("layers", schemaName);
        _fieldsTable = Infrastructure.SchemaSearchPath.QualifyTable("layer_fields", schemaName);
        _servicesTable = Infrastructure.SchemaSearchPath.QualifyTable("services", schemaName);
        _serviceLayersTable = Infrastructure.SchemaSearchPath.QualifyTable("service_layers", schemaName);
        _relationshipsTable = Infrastructure.SchemaSearchPath.QualifyTable("relationships", schemaName);
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_column,
                l.storage_srid,
                l.temporal_column,
                l.storage_options,
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
              AND l.enabled = TRUE
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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

        return PopulateLayerDetails(layer, fields, relationships);
    }

    /// <inheritdoc />
    public async Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_column,
                l.storage_srid,
                l.temporal_column,
                l.storage_options,
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
            WHERE l.enabled = TRUE
            ORDER BY l.layer_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        return [.. layers.Select(layer => PopulateLayerDetails(
            layer,
            fieldsMap.TryGetValue(layer.Id, out FieldDefinition[]? fields) ? fields : [],
            relationshipsMap.TryGetValue(layer.Id, out Relationship[]? relationships) ? relationships : []))];
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        string sql = $"SELECT 1 FROM {_layersTable} WHERE layer_id = @layerId AND enabled = TRUE";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    /// <inheritdoc />
    public async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string sql = $"SELECT 1 FROM {_servicesTable} WHERE LOWER(service_name) = LOWER(@serviceName)";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        int extentSrid = ReadExtentSrid(reader, srid);
        double? minScale = reader.IsDBNull(reader.GetOrdinal("min_scale")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("min_scale"));
        double? maxScale = reader.IsDBNull(reader.GetOrdinal("max_scale")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("max_scale"));
        bool defaultVisibility = reader.GetBoolean(reader.GetOrdinal("default_visibility"));

        FeatureExtent? extent = ReadExtent(reader, extentSrid);

        var spatialReference = SpatialReference.Create(srid);

        var storageMapping = ReadStorageMapping(reader, srid);

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
            StorageMapping: storageMapping);
    }

    private static ServiceDefinition ReadServiceDefinition(NpgsqlDataReader reader)
    {
        string name = reader.GetString(reader.GetOrdinal("service_name"));
        string description = reader.GetString(reader.GetOrdinal("description"));
        int srid = reader.GetInt32(reader.GetOrdinal("srid"));
        Guid? connectionId = reader.IsDBNull(reader.GetOrdinal("connection_id"))
            ? null
            : reader.GetGuid(reader.GetOrdinal("connection_id"));
        int extentSrid = ReadExtentSrid(reader, srid);
        string[] supportedFormats = reader.GetFieldValue<string[]>(reader.GetOrdinal("supported_formats"));
        string[] capabilities = reader.GetFieldValue<string[]>(reader.GetOrdinal("capabilities"));

        FeatureExtent? extent = ReadExtent(reader, extentSrid);

        var spatialReference = SpatialReference.Create(srid);

        return new ServiceDefinition(
            name,
            description,
            [], // Layers populated separately
            spatialReference,
            supportedFormats,
            capabilities,
            extent,
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
                f.description,
                f.domain,
                f.hidden
            FROM {_fieldsTable} f
            WHERE f.layer_id = @layerId
            ORDER BY f.field_order
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var fields = new List<FieldDefinition>();

        while (await reader.ReadAsync(cancellationToken))
        {
            fields.Add(ReadFieldDefinition(reader));
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
                f.description,
                f.domain,
                f.hidden
            FROM {_fieldsTable} f
            WHERE f.layer_id = ANY(@layerIds)
            ORDER BY f.layer_id, f.field_order
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerIds", layerIds);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var fieldsMap = new Dictionary<int, List<FieldDefinition>>();

        while (await reader.ReadAsync(cancellationToken))
        {
            int layerId = reader.GetInt32(reader.GetOrdinal("layer_id"));

            if (!fieldsMap.TryGetValue(layerId, out List<FieldDefinition>? fieldsList))
            {
                fieldsList = [];
                fieldsMap[layerId] = fieldsList;
            }

            fieldsList.Add(ReadFieldDefinition(reader));
        }

        return fieldsMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    private async Task<LayerDefinition[]> GetServiceLayersAsync(string serviceName, CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT
                l.layer_id, l.layer_name, l.description, l.geometry_type, l.srid,
                l.table_schema, l.table_name, l.primary_key_column, l.geometry_column,
                l.storage_srid, l.temporal_column, l.storage_options,
                l.min_scale, l.max_scale, l.default_visibility, l.metadata,
                ST_XMin(l.extent) as xmin, ST_YMin(l.extent) as ymin,
                ST_XMax(l.extent) as xmax, ST_YMax(l.extent) as ymax,
                ST_SRID(l.extent) as extent_srid
            FROM {_serviceLayersTable} sl
            JOIN {_layersTable} l ON sl.layer_id = l.layer_id
            WHERE LOWER(sl.service_name) = LOWER(@serviceName)
              AND l.enabled = TRUE
            ORDER BY sl.layer_order, l.layer_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@serviceName", serviceName);

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

        if (layerIds.Count == 0)
            return [];

        Dictionary<int, FieldDefinition[]> fieldsMap = await GetLayerFieldsBatchAsync([.. layerIds], cancellationToken);
        Dictionary<int, Relationship[]> relationshipsMap = await GetLayerRelationshipsBatchAsync([.. layerIds], cancellationToken);

        return [.. layers.Select(layer => PopulateLayerDetails(
            layer,
            fieldsMap.TryGetValue(layer.Id, out FieldDefinition[]? fields) ? fields : [],
            relationshipsMap.TryGetValue(layer.Id, out Relationship[]? relationships) ? relationships : []))];
    }

    private async Task<Dictionary<string, LayerDefinition[]>> GetServiceLayersBatchAsync(string[] serviceNames, CancellationToken cancellationToken)
    {
        if (serviceNames.Length == 0)
            return [];

        string sql = $"""
            SELECT
                sl.service_name,
                l.layer_id, l.layer_name, l.description, l.geometry_type, l.srid,
                l.table_schema, l.table_name, l.primary_key_column, l.geometry_column,
                l.storage_srid, l.temporal_column, l.storage_options,
                l.min_scale, l.max_scale, l.default_visibility, l.metadata,
                ST_XMin(l.extent) as xmin, ST_YMin(l.extent) as ymin,
                ST_XMax(l.extent) as xmax, ST_YMax(l.extent) as ymax,
                ST_SRID(l.extent) as extent_srid
            FROM {_serviceLayersTable} sl
            JOIN {_layersTable} l ON sl.layer_id = l.layer_id
            WHERE LOWER(sl.service_name) = ANY(@serviceNames)
              AND l.enabled = TRUE
            ORDER BY sl.service_name, sl.layer_order, l.layer_id
            """;

        string[] loweredNames = [.. serviceNames.Select(n => n.ToLowerInvariant())];

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@serviceNames", loweredNames);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var layersMap = new Dictionary<string, List<LayerDefinition>>();
        var allLayerIds = new HashSet<int>();

        while (await reader.ReadAsync(cancellationToken))
        {
            string serviceName = reader.GetString(reader.GetOrdinal("service_name"));
            LayerDefinition layer = ReadLayerDefinition(reader);

            if (!layersMap.TryGetValue(serviceName, out List<LayerDefinition>? layerList))
            {
                layerList = [];
                layersMap[serviceName] = layerList;
            }

            layerList.Add(layer);
            allLayerIds.Add(layer.Id);
        }
        reader.Close();

        if (allLayerIds.Count == 0)
            return layersMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());

        Dictionary<int, FieldDefinition[]> fieldsMap = await GetLayerFieldsBatchAsync([.. allLayerIds], cancellationToken);
        Dictionary<int, Relationship[]> relationshipsMap = await GetLayerRelationshipsBatchAsync([.. allLayerIds], cancellationToken);

        return layersMap.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Select(layer => PopulateLayerDetails(
                layer,
                fieldsMap.TryGetValue(layer.Id, out FieldDefinition[]? fields) ? fields : [],
                relationshipsMap.TryGetValue(layer.Id, out Relationship[]? relationships) ? relationships : [])).ToArray());
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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

    private static int ReadExtentSrid(NpgsqlDataReader reader, int fallbackSrid)
    {
        int extentSridOrdinal = reader.GetOrdinal("extent_srid");
        if (!reader.IsDBNull(extentSridOrdinal))
        {
            int extentSrid = reader.GetInt32(extentSridOrdinal);
            if (extentSrid > 0)
                return extentSrid;
        }
        return fallbackSrid;
    }

    private static FeatureExtent? ReadExtent(NpgsqlDataReader reader, int extentSrid)
    {
        int xminOrdinal = reader.GetOrdinal("xmin");
        if (reader.IsDBNull(xminOrdinal))
            return null;

        double xmin = reader.GetDouble(xminOrdinal);
        double ymin = reader.GetDouble(reader.GetOrdinal("ymin"));
        double xmax = reader.GetDouble(reader.GetOrdinal("xmax"));
        double ymax = reader.GetDouble(reader.GetOrdinal("ymax"));
        return FeatureExtent.Create(xmin, ymin, xmax, ymax, extentSrid);
    }

    private static FieldDefinition ReadFieldDefinition(NpgsqlDataReader reader)
    {
        string fieldName = reader.GetString(reader.GetOrdinal("field_name"));

        string fieldTypeString = reader.GetString(reader.GetOrdinal("field_type"));
        if (!Enum.TryParse<FieldType>(fieldTypeString, ignoreCase: true, out FieldType fieldType))
            throw new InvalidDataException($"Invalid field type: {fieldTypeString}");

        int maxLengthOrdinal = reader.GetOrdinal("max_length");
        int? maxLength = reader.IsDBNull(maxLengthOrdinal) ? null : (int?)reader.GetInt32(maxLengthOrdinal);
        bool nullable = reader.GetBoolean(reader.GetOrdinal("nullable"));
        int defaultValueOrdinal = reader.GetOrdinal("default_value");
        object? defaultValue = reader.IsDBNull(defaultValueOrdinal) ? null : reader.GetValue(defaultValueOrdinal);
        int descriptionOrdinal = reader.GetOrdinal("description");
        string? description = reader.IsDBNull(descriptionOrdinal) ? null : reader.GetString(descriptionOrdinal);
        int domainOrdinal = reader.GetOrdinal("domain");
        FieldDomainDefinition? domain = reader.IsDBNull(domainOrdinal)
            ? null
            : JsonSerializer.Deserialize(
                reader.GetString(domainOrdinal),
                CatalogJsonContext.Default.FieldDomainDefinition);
        bool hidden = reader.GetBoolean(reader.GetOrdinal("hidden"));

        return new FieldDefinition(fieldName, fieldType, maxLength, nullable, defaultValue, description, domain, hidden);
    }

    private static LayerDefinition PopulateLayerDetails(
        LayerDefinition layer,
        FieldDefinition[] fields,
        Relationship[] relationships)
    {
        var storageMapping = layer.StorageMapping;
        if (storageMapping != null)
        {
            var primaryKeyColumn = storageMapping.PrimaryKeyColumn;
            if (string.Equals(primaryKeyColumn, FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) &&
                !fields.Any(field => field.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase)))
            {
                primaryKeyColumn = ResolvePrimaryKeyColumn(fields);
            }

            var geometryColumn = storageMapping.GeometryColumn;
            if (layer.GeometryType != GeometryType.None)
            {
                geometryColumn = fields.FirstOrDefault(field => field.IsGeometry)?.Name ?? geometryColumn;
            }

            storageMapping = storageMapping with
            {
                PrimaryKeyColumn = primaryKeyColumn,
                GeometryColumn = geometryColumn,
                StorageSrid = storageMapping.StorageSrid ?? layer.SpatialReference.Wkid
            };
        }

        return layer with
        {
            Fields = fields,
            Relationships = relationships,
            StorageMapping = storageMapping
        };
    }

    private static string ResolvePrimaryKeyColumn(FieldDefinition[] fields)
    {
        var primaryKey = fields.FirstOrDefault(field =>
            field.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
            field.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) ||
            field.Name.Equals("fid", StringComparison.OrdinalIgnoreCase)) ??
            fields.FirstOrDefault(field => field.Type is FieldType.Integer or FieldType.BigInteger);

        return primaryKey?.Name ?? FieldNames.ObjectId;
    }

    private static LayerStorageMapping ReadStorageMapping(NpgsqlDataReader reader, int defaultSrid)
    {
        var tableName = reader.GetString(reader.GetOrdinal("table_name"));
        var schemaName = ReadNullableString(reader, "table_schema");
        var primaryKeyColumn = ReadNullableString(reader, "primary_key_column") ?? FieldNames.ObjectId;
        var geometryColumn = ReadNullableString(reader, "geometry_column");
        var temporalColumn = ReadNullableString(reader, "temporal_column");

        var storageSridOrdinal = reader.GetOrdinal("storage_srid");
        var storageSrid = reader.IsDBNull(storageSridOrdinal)
            ? defaultSrid
            : reader.GetInt32(storageSridOrdinal);

        return new LayerStorageMapping(
            tableName,
            SchemaName: schemaName,
            PrimaryKeyColumn: primaryKeyColumn,
            GeometryColumn: geometryColumn,
            StorageSrid: storageSrid,
            TemporalColumn: temporalColumn,
            ProviderOptions: ReadStorageOptions(reader));
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Dictionary<string, string> ReadStorageOptions(NpgsqlDataReader reader)
    {
        var ordinal = reader.GetOrdinal("storage_options");
        if (reader.IsDBNull(ordinal))
        {
            return new Dictionary<string, string>();
        }

        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        return JsonSerializer.Deserialize(json, CatalogJsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>();
    }

}
