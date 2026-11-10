// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Styling;

/// <summary>
/// PostgreSQL implementation of layer style persistence.
/// </summary>
internal sealed class PostgresLayerStyleCatalog : ILayerStyleCatalog
{
    private const string SelectColumns = """
        layer_id,
        maplibre_style,
        geoservices_drawing_info,
        style_version,
        style_revised_at,
        style_revised_by,
        style_change_summary
        """;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _layersTable;
    private readonly string _stylesTable;
    private readonly string _refsTable;

    public PostgresLayerStyleCatalog(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

        _layersTable = Infrastructure.SchemaSearchPath.QualifyTable("layers", schemaName);
        _stylesTable = Infrastructure.SchemaSearchPath.QualifyTable("styles", schemaName);
        _refsTable = Infrastructure.SchemaSearchPath.QualifyTable("layer_style_refs", schemaName);
    }

    /// <inheritdoc />
    public async Task<LayerStyleDefinition?> GetLayerStyleAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            SELECT layers.layer_id,
                CASE WHEN primary_style.style_id IS NULL THEN layers.maplibre_style ELSE primary_style.maplibre_style END,
                CASE WHEN primary_style.style_id IS NULL THEN layers.geoservices_drawing_info ELSE primary_style.drawing_info END,
                CASE WHEN primary_style.style_id IS NULL THEN layers.style_version ELSE primary_style.style_version END,
                CASE WHEN primary_style.style_id IS NULL THEN layers.style_revised_at ELSE primary_style.updated_at END,
                CASE WHEN primary_style.style_id IS NULL THEN layers.style_revised_by ELSE primary_style.revised_by END,
                CASE WHEN primary_style.style_id IS NULL THEN layers.style_change_summary ELSE primary_style.change_summary END,
                primary_style.layer_ids
            FROM {_layersTable} layers
            LEFT JOIN LATERAL (
                SELECT styles.style_id, styles.maplibre_style, styles.drawing_info,
                    styles.style_version, styles.updated_at, styles.revised_by, styles.change_summary,
                    ARRAY(SELECT DISTINCT other.layer_id FROM {_refsTable} other WHERE other.style_id = styles.style_id) AS layer_ids
                FROM {_refsTable} refs
                JOIN {_stylesTable} styles ON styles.style_id = refs.style_id
                WHERE refs.layer_id = layers.layer_id AND refs.ordinal = 0
                ORDER BY styles.style_id
                LIMIT 1
            ) primary_style ON TRUE
            WHERE layers.layer_id = @layerId
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var style = ReadStyle(reader);
        return style with
        {
            MapLibreStyleJson = CatalogStyleSourceProjection.ForLayer(style.MapLibreStyleJson, layerId,
            reader.IsDBNull(7) ? [] : reader.GetFieldValue<int[]>(7))
        };
    }

    /// <inheritdoc />
    public async Task<LayerStyleDefinition?> SetMapLibreStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        string sql = $"""
            UPDATE {_layersTable}
            SET maplibre_style = @mapLibreStyle,
                geoservices_drawing_info = NULL,
                style_version = COALESCE(style_version, 0) + 1,
                style_revised_at = NOW(),
                style_revised_by = @revisedBy,
                style_change_summary = @changeSummary
            WHERE layer_id = @layerId
            RETURNING {SelectColumns}
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.Add(new NpgsqlParameter("@mapLibreStyle", NpgsqlDbType.Jsonb)
        {
            Value = mapLibreStyleJson
        });
        AddNullableText(command, "@revisedBy", revisedBy);
        AddNullableText(command, "@changeSummary", changeSummary);

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
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        string sql = $"""
            UPDATE {_layersTable}
            SET maplibre_style = @mapLibreStyle,
                geoservices_drawing_info = @drawingInfo,
                style_version = COALESCE(style_version, 0) + 1,
                style_revised_at = NOW(),
                style_revised_by = @revisedBy,
                style_change_summary = @changeSummary
            WHERE layer_id = @layerId
            RETURNING {SelectColumns}
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        AddNullableText(command, "@revisedBy", revisedBy);
        AddNullableText(command, "@changeSummary", changeSummary);

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
                AND NOT EXISTS (
                    SELECT 1 FROM {_refsTable}
                    WHERE layer_id = @layerId AND ordinal = 0
                )
            RETURNING {SelectColumns}
            """;

        await using (var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var command = new NpgsqlCommand(sql, connection);
            _ = command.Parameters.AddWithValue("@layerId", layerId);
            _ = command.Parameters.Add(new NpgsqlParameter("@drawingInfo", NpgsqlDbType.Jsonb)
            {
                Value = drawingInfoJson
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return ReadStyle(reader);
        }
        return await GetLayerStyleAsync(layerId, cancellationToken).ConfigureAwait(false);
    }

    private static void AddNullableText(NpgsqlCommand command, string name, string? value)
    {
        var parameter = new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value
        };
        _ = command.Parameters.Add(parameter);
    }

    private static LayerStyleDefinition ReadStyle(NpgsqlDataReader reader)
    {
        return new LayerStyleDefinition
        {
            LayerId = reader.GetInt32(0),
            MapLibreStyleJson = reader.IsDBNull(1) ? null : reader.GetString(1),
            DrawingInfoJson = reader.IsDBNull(2) ? null : reader.GetString(2),
            StyleVersion = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            StyleRevisedAt = reader.IsDBNull(4)
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
            StyleRevisedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
            StyleChangeSummary = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }
}
