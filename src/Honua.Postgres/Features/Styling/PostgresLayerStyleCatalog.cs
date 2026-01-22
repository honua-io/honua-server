// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Styling;

/// <summary>
/// PostgreSQL implementation of layer style persistence.
/// </summary>
internal sealed class PostgresLayerStyleCatalog : ILayerStyleCatalog
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _layersTable;

    public PostgresLayerStyleCatalog(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

        string schema = string.IsNullOrEmpty(schemaName) ? "honua" : schemaName;
        _layersTable = $"{schema}.layers";
    }

    /// <inheritdoc />
    public async Task<LayerStyleDefinition?> GetLayerStyleAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT layer_id,
                   maplibre_style,
                   geoservices_drawing_info,
                   style_version
            FROM {_layersTable}
            WHERE layer_id = @layerId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadStyle(reader);
    }

    /// <inheritdoc />
    public async Task<LayerStyleDefinition?> SetMapLibreStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        CancellationToken cancellationToken = default)
    {
        string sql = $"""
            UPDATE {_layersTable}
            SET maplibre_style = @mapLibreStyle,
                geoservices_drawing_info = NULL,
                style_version = COALESCE(style_version, 0) + 1
            WHERE layer_id = @layerId
            RETURNING layer_id,
                      maplibre_style,
                      geoservices_drawing_info,
                      style_version
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.Add(new NpgsqlParameter("@mapLibreStyle", NpgsqlDbType.Jsonb)
        {
            Value = mapLibreStyleJson
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadStyle(reader);
    }

    /// <inheritdoc />
    public async Task<LayerStyleDefinition?> SetStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string drawingInfoJson,
        CancellationToken cancellationToken = default)
    {
        string sql = $"""
            UPDATE {_layersTable}
            SET maplibre_style = @mapLibreStyle,
                geoservices_drawing_info = @drawingInfo,
                style_version = COALESCE(style_version, 0) + 1
            WHERE layer_id = @layerId
            RETURNING layer_id,
                      maplibre_style,
                      geoservices_drawing_info,
                      style_version
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.Add(new NpgsqlParameter("@mapLibreStyle", NpgsqlDbType.Jsonb)
        {
            Value = mapLibreStyleJson
        });
        _ = command.Parameters.Add(new NpgsqlParameter("@drawingInfo", NpgsqlDbType.Jsonb)
        {
            Value = drawingInfoJson
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadStyle(reader);
    }

    /// <inheritdoc />
    public async Task<LayerStyleDefinition?> SetDrawingInfoAsync(
        int layerId,
        string drawingInfoJson,
        CancellationToken cancellationToken = default)
    {
        string sql = $"""
            UPDATE {_layersTable}
            SET geoservices_drawing_info = @drawingInfo
            WHERE layer_id = @layerId
            RETURNING layer_id,
                      maplibre_style,
                      geoservices_drawing_info,
                      style_version
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.Add(new NpgsqlParameter("@drawingInfo", NpgsqlDbType.Jsonb)
        {
            Value = drawingInfoJson
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadStyle(reader);
    }

    private static LayerStyleDefinition ReadStyle(NpgsqlDataReader reader)
    {
        return new LayerStyleDefinition
        {
            LayerId = reader.GetInt32(0),
            MapLibreStyleJson = reader.IsDBNull(1) ? null : reader.GetString(1),
            DrawingInfoJson = reader.IsDBNull(2) ? null : reader.GetString(2),
            StyleVersion = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
        };
    }
}
