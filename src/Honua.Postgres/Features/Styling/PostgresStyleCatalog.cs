// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Styling;

/// <summary>
/// PostgreSQL implementation of the independent, styleId-keyed style catalog
/// (ADR-0048, Phase 2). Backed by <c>honua.styles</c> and the ordered
/// <c>honua.layer_style_refs</c> association table (migration 042).
/// </summary>
internal sealed class PostgresStyleCatalog : IStyleCatalog
{
    private const string SelectColumns = """
        style_id,
        title,
        description,
        maplibre_style,
        drawing_info,
        style_version,
        created_at,
        updated_at,
        revised_by,
        change_summary
        """;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _stylesTable;
    private readonly string _refsTable;
    private readonly string _layersTable;

    public PostgresStyleCatalog(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _stylesTable = Infrastructure.SchemaSearchPath.QualifyTable("styles", schemaName);
        _refsTable = Infrastructure.SchemaSearchPath.QualifyTable("layer_style_refs", schemaName);
        _layersTable = Infrastructure.SchemaSearchPath.QualifyTable("layers", schemaName);
    }

    /// <inheritdoc />
    public async Task<StyleCatalogRecord?> GetStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);

        var sql = $"""
            SELECT {SelectColumns}
            FROM {_stylesTable}
            WHERE style_id = @styleId
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@styleId", styleId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadStyle(reader);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StyleCatalogRecord>> ListStylesAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
            FROM {_stylesTable}
            ORDER BY style_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        var results = new List<StyleCatalogRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadStyle(reader));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StyleCatalogRecord>> GetStylesForLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {QualifiedSelectColumns("s")}
            FROM {_refsTable} r
            JOIN {_stylesTable} s ON s.style_id = r.style_id
            WHERE r.layer_id = @layerId
            ORDER BY r.ordinal, s.style_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        var results = new List<StyleCatalogRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadStyle(reader));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StyleLayerAssociation>> ListAssociationsAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT layer_id, style_id, ordinal
            FROM {_refsTable}
            ORDER BY layer_id, ordinal, style_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        var results = new List<StyleLayerAssociation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new StyleLayerAssociation(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<StyleCatalogRecord?> CreateStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        string? title = null,
        string? description = null,
        string? drawingInfoJson = null,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapLibreStyleJson);

        var sql = $"""
            INSERT INTO {_stylesTable}
                (style_id, title, description, maplibre_style, drawing_info,
                 style_version, created_at, updated_at, revised_by, change_summary)
            VALUES
                (@styleId, @title, @description, @mapLibreStyle, @drawingInfo,
                 1, NOW(), NOW(), @revisedBy, @changeSummary)
            ON CONFLICT (style_id) DO NOTHING
            RETURNING {SelectColumns}
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@styleId", styleId);
        AddNullableText(command, "@title", title);
        AddNullableText(command, "@description", description);
        AddJsonb(command, "@mapLibreStyle", mapLibreStyleJson);
        AddNullableJsonb(command, "@drawingInfo", drawingInfoJson);
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
    public async Task<StyleCatalogRecord> UpsertStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        string? title = null,
        string? description = null,
        string? drawingInfoJson = null,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapLibreStyleJson);

        var sql = $"""
            INSERT INTO {_stylesTable}
                (style_id, title, description, maplibre_style, drawing_info,
                 style_version, created_at, updated_at, revised_by, change_summary)
            VALUES
                (@styleId, @title, @description, @mapLibreStyle, @drawingInfo,
                 1, NOW(), NOW(), @revisedBy, @changeSummary)
            ON CONFLICT (style_id) DO UPDATE SET
                title = COALESCE(EXCLUDED.title, {_stylesTable}.title),
                description = COALESCE(EXCLUDED.description, {_stylesTable}.description),
                maplibre_style = EXCLUDED.maplibre_style,
                drawing_info = EXCLUDED.drawing_info,
                style_version = {_stylesTable}.style_version + 1,
                updated_at = NOW(),
                revised_by = EXCLUDED.revised_by,
                change_summary = EXCLUDED.change_summary
            RETURNING {SelectColumns}
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@styleId", styleId);
        AddNullableText(command, "@title", title);
        AddNullableText(command, "@description", description);
        AddJsonb(command, "@mapLibreStyle", mapLibreStyleJson);
        AddNullableJsonb(command, "@drawingInfo", drawingInfoJson);
        AddNullableText(command, "@revisedBy", revisedBy);
        AddNullableText(command, "@changeSummary", changeSummary);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadStyle(reader);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);

        // layer_style_refs cascades on delete (FK ON DELETE CASCADE).
        var sql = $"""
            DELETE FROM {_stylesTable}
            WHERE style_id = @styleId
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@styleId", styleId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> AssociateLayerAsync(int layerId, string styleId, int ordinal = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);

        var sql = $"""
            INSERT INTO {_refsTable} (layer_id, style_id, ordinal)
            SELECT @layerId, @styleId, @ordinal
            WHERE EXISTS (SELECT 1 FROM {_stylesTable} WHERE style_id = @styleId)
              AND EXISTS (SELECT 1 FROM {_layersTable} WHERE layer_id = @layerId)
            ON CONFLICT (layer_id, style_id) DO UPDATE SET ordinal = EXCLUDED.ordinal
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.AddWithValue("@styleId", styleId);
        _ = command.Parameters.AddWithValue("@ordinal", ordinal);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    private static string QualifiedSelectColumns(string alias) =>
        string.Join(
            ",\n",
            SelectColumns
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(column => $"{alias}.{column.TrimEnd(',')}"));

    private static void AddJsonb(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = value });

    private static void AddNullableJsonb(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value
        });

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value
        });

    private static StyleCatalogRecord ReadStyle(NpgsqlDataReader reader)
    {
        return new StyleCatalogRecord
        {
            StyleId = reader.GetString(0),
            Title = reader.IsDBNull(1) ? null : reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            MapLibreStyleJson = reader.GetString(3),
            DrawingInfoJson = reader.IsDBNull(4) ? null : reader.GetString(4),
            StyleVersion = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            CreatedAt = ReadUtc(reader, 6),
            UpdatedAt = ReadUtc(reader, 7),
            RevisedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
            ChangeSummary = reader.IsDBNull(9) ? null : reader.GetString(9)
        };
    }

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? default
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
