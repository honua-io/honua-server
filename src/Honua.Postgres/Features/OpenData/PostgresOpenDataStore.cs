// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.OpenData.Abstractions;
using Honua.Core.Features.OpenData.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.OpenData;

/// <summary>
/// PostgreSQL-backed store for Console open-data page and STAC publication state.
/// </summary>
internal sealed class PostgresOpenDataStore : IOpenDataStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _pagesTable;
    private readonly string _stacPublicationsTable;

    public PostgresOpenDataStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _pagesTable = SchemaSearchPath.QualifyTable("open_data_pages", schemaName);
        _stacPublicationsTable = SchemaSearchPath.QualifyTable("open_data_stac_publications", schemaName);
    }

    public async Task<OpenDataPageRecord?> GetPageRecordAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var sql = $"SELECT page_json FROM {_pagesTable} WHERE item_id = @item_id";
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@item_id", NpgsqlDbType.Text, itemId);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : DeserializePage(json);
    }

    public async Task<OpenDataPageRecord> SetPageRecordAsync(
        OpenDataPageRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var json = JsonSerializer.Serialize(record, OpenDataDomainJsonContext.Default.OpenDataPageRecord);
        var sql = $"""
            INSERT INTO {_pagesTable}
                (item_id, is_published, updated_at, updated_by, page_json)
            VALUES
                (@item_id, @is_published, @updated_at, @updated_by, @page_json)
            ON CONFLICT (item_id) DO UPDATE SET
                is_published = EXCLUDED.is_published,
                updated_at   = EXCLUDED.updated_at,
                updated_by   = EXCLUDED.updated_by,
                page_json    = EXCLUDED.page_json
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@item_id", NpgsqlDbType.Text, record.ItemId);
        command.Parameters.AddWithValue("@is_published", NpgsqlDbType.Boolean, record.IsPublished);
        command.Parameters.AddWithValue("@updated_at", NpgsqlDbType.TimestampTz, record.UpdatedAt);
        command.Parameters.AddWithValue("@updated_by", NpgsqlDbType.Text, (object?)record.UpdatedBy ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("@page_json", NpgsqlDbType.Jsonb) { Value = json });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<OpenDataPageRecord>> ListPageRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT page_json FROM {_pagesTable} ORDER BY item_id";
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var records = new List<OpenDataPageRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(DeserializePage(reader.GetString(0)));
        }

        return records;
    }

    public async Task<OpenDataStacPublicationRecord?> GetStacPublicationAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return null;
        }

        var sql = $"SELECT publication_json FROM {_stacPublicationsTable} WHERE collection_id = @collection_id";
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@collection_id", NpgsqlDbType.Text, collectionId);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : DeserializeStacPublication(json);
    }

    public async Task<OpenDataStacPublicationRecord?> GetStacPublicationByItemAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var sql = $"""
            SELECT publication_json
            FROM {_stacPublicationsTable}
            WHERE item_id = @item_id
            ORDER BY updated_at DESC, collection_id
            LIMIT 1
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@item_id", NpgsqlDbType.Text, itemId);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : DeserializeStacPublication(json);
    }

    public async Task<OpenDataStacPublicationRecord> SetStacPublicationAsync(
        OpenDataStacPublicationRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var json = JsonSerializer.Serialize(
            record,
            OpenDataDomainJsonContext.Default.OpenDataStacPublicationRecord);
        var sql = $"""
            INSERT INTO {_stacPublicationsTable}
                (collection_id, item_id, status, updated_at, publication_json)
            VALUES
                (@collection_id, @item_id, @status, @updated_at, @publication_json)
            ON CONFLICT (collection_id) DO UPDATE SET
                item_id           = EXCLUDED.item_id,
                status            = EXCLUDED.status,
                updated_at        = EXCLUDED.updated_at,
                publication_json  = EXCLUDED.publication_json
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@collection_id", NpgsqlDbType.Text, record.CollectionId);
        command.Parameters.AddWithValue("@item_id", NpgsqlDbType.Text, record.ItemId);
        command.Parameters.AddWithValue("@status", NpgsqlDbType.Text, record.Status.ToString());
        command.Parameters.AddWithValue("@updated_at", NpgsqlDbType.TimestampTz, record.UpdatedAt);
        command.Parameters.Add(new NpgsqlParameter("@publication_json", NpgsqlDbType.Jsonb) { Value = json });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<OpenDataStacPublicationRecord>> ListStacPublicationsAsync(
        CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT publication_json FROM {_stacPublicationsTable} ORDER BY collection_id";
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var records = new List<OpenDataStacPublicationRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(DeserializeStacPublication(reader.GetString(0)));
        }

        return records;
    }

    private static OpenDataPageRecord DeserializePage(string json)
        => JsonSerializer.Deserialize(json, OpenDataDomainJsonContext.Default.OpenDataPageRecord)
           ?? throw new InvalidDataException("Stored open-data page record is empty.");

    private static OpenDataStacPublicationRecord DeserializeStacPublication(string json)
        => JsonSerializer.Deserialize(json, OpenDataDomainJsonContext.Default.OpenDataStacPublicationRecord)
           ?? throw new InvalidDataException("Stored open-data STAC publication record is empty.");
}
