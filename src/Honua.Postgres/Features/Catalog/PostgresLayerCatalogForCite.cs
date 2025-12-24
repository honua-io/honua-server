// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.Catalog;

/// <summary>
/// Simple PostgreSQL layer catalog for CITE conformance testing
/// Works with the basic schema from 001_CreateHonuaSchema.sql migration
/// </summary>
internal sealed class PostgresLayerCatalogForCite : ILayerCatalog
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _schema;

    public PostgresLayerCatalogForCite(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _schema = string.IsNullOrEmpty(schemaName) ? "honua" : schemaName;
    }

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
                ST_XMin(l.extent) as xmin,
                ST_YMin(l.extent) as ymin,
                ST_XMax(l.extent) as xmax,
                ST_YMax(l.extent) as ymax
            FROM {_schema}.layers l
            ORDER BY l.layer_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        var layers = new List<LayerDefinition>();

        while (await reader.ReadAsync(cancellationToken))
        {
            layers.Add(ReadLayerDefinition(reader));
        }

        return [.. layers];
    }

    // Simple stub implementations for other methods
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
                ST_XMin(l.extent) as xmin,
                ST_YMin(l.extent) as ymin,
                ST_XMax(l.extent) as xmax,
                ST_YMax(l.extent) as ymax
            FROM {_schema}.layers l
            WHERE l.layer_id = @layerId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("layerId", layerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadLayerDefinition(reader);
    }

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult<ServiceDefinition?>(null);

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ServiceDefinition[]>([]);

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        => Task.FromResult<Relationship?>(null);

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult<Relationship[]>([]);

    private static LayerDefinition ReadLayerDefinition(NpgsqlDataReader reader)
    {
        int layerId = reader.GetInt32(reader.GetOrdinal("layer_id"));
        string layerName = reader.GetString(reader.GetOrdinal("layer_name"));

        int descOrdinal = reader.GetOrdinal("description");
        string? description = reader.IsDBNull(descOrdinal) ? null : reader.GetString(descOrdinal);

        string geometryTypeStr = reader.GetString(reader.GetOrdinal("geometry_type"));
        int srid = reader.GetInt32(reader.GetOrdinal("srid"));
        bool defaultVisibility = reader.GetBoolean(reader.GetOrdinal("default_visibility"));

        if (!Enum.TryParse<GeometryType>(geometryTypeStr, true, out var geometryType))
        {
            geometryType = GeometryType.Point;
        }

        var spatialReference = new SpatialReference(srid);

        return new LayerDefinition(
            layerId,
            layerName,
            description ?? string.Empty,
            geometryType,
            spatialReference,
            [], // Fields - No fields for now - basic CITE testing
            null, // Extent - Not reading extent for simplified version
            null, // MinScale
            null, // MaxScale
            defaultVisibility,
            [] // Relationships - No relationships for basic CITE testing
        );
    }
}
