// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Metadata;

internal sealed partial class PostgresMetadataV2GraphStore : IMetadataFieldHistoryStore
{
    private const int DefaultHistoryLimit = 50;
    private const int MaxHistoryLimit = 200;

    public async Task<MetadataFieldHistoryPage> ListAsync(
        MetadataFieldHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit <= 0 ? DefaultHistoryLimit : query.Limit, 1, MaxHistoryLimit);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = BuildHistoryCommand(connection, query, limit);
        var items = new List<MetadataFieldHistoryEntry>(limit + 1);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadHistoryEntry(reader));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new MetadataFieldHistoryPage { Items = [], HasMore = false };
        }

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new MetadataFieldHistoryPage
        {
            Items = items,
            HasMore = hasMore,
            NextBeforeId = hasMore && items.Count > 0 ? items[^1].Id : null,
        };
    }

    // One extra row tells the caller whether another page exists.
    private NpgsqlCommand BuildHistoryCommand(NpgsqlConnection connection, MetadataFieldHistoryQuery query, int limit)
    {
        var sql = new StringBuilder($"""
            SELECT id, revision, item_kind, item_id, field_path, old_value::text, new_value::text, actor, changed_at
            FROM {_fieldHistoryTable}
            WHERE environment = @environment
            """);
        var command = new NpgsqlCommand { Connection = connection };
        command.Parameters.AddWithValue("@environment", _environment);

        AppendItemFilters(sql, command, query);
        if (query.BeforeId is { } beforeId)
        {
            sql.Append(" AND id < @before_id");
            command.Parameters.AddWithValue("@before_id", beforeId);
        }

        sql.Append(" ORDER BY id DESC LIMIT @limit");
        command.Parameters.AddWithValue("@limit", limit + 1);
        command.CommandText = sql.ToString();
        return command;
    }

    private static void AppendItemFilters(StringBuilder sql, NpgsqlCommand command, MetadataFieldHistoryQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.ItemKind))
        {
            sql.Append(" AND item_kind = ANY(@item_kinds)");
            command.Parameters.Add(new NpgsqlParameter("@item_kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ExpandItemKinds(query.ItemKind) });
        }

        if (!string.IsNullOrWhiteSpace(query.ItemId))
        {
            sql.Append(" AND item_id = @item_id");
            command.Parameters.AddWithValue("@item_id", query.ItemId.Trim());
        }

        var prefix = (query.FieldPrefix ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (prefix.Length > 0)
        {
            sql.Append(" AND field_path[1:@prefix_len] = @field_prefix");
            command.Parameters.AddWithValue("@prefix_len", prefix.Length);
            command.Parameters.Add(new NpgsqlParameter("@field_prefix", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = prefix });
        }
    }

    private static MetadataFieldHistoryEntry ReadHistoryEntry(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Revision = reader.GetInt64(1),
        ItemKind = reader.GetString(2),
        ItemId = reader.GetString(3),
        FieldPath = reader.GetFieldValue<string[]>(4),
        OldValue = reader.IsDBNull(5) ? null : ParseJsonElement(reader.GetString(5)),
        NewValue = reader.IsDBNull(6) ? null : ParseJsonElement(reader.GetString(6)),
        Actor = reader.GetString(7),
        ChangedAt = reader.GetFieldValue<DateTimeOffset>(8),
    };

    private static string[] ExpandItemKinds(string itemKind) => itemKind.Trim().ToLowerInvariant() switch
    {
        "resource" or "resources" or "object" or "objects" or "dataset" or "datasets" => ["resources", "objects"],
        "service" or "services" => ["services"],
        "publication" or "publications" or "layer" or "layers" => ["publications", "layers"],
        "folder" or "folders" => ["folders"],
        "catalog" or "catalogs" => ["catalogs"],
        "graph" or "environment" => ["graph"],
        var other => [other],
    };

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
