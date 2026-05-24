// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Observability;

/// <summary>
/// PostgreSQL implementation of <see cref="IInvestigationStore"/> backing the Console
/// Operate investigation API (#1168).
/// </summary>
internal sealed class PostgresInvestigationStore : IInvestigationStore
{
    internal const int MinPageSize = 1;
    internal const int MaxPageSize = 200;
    internal const int DefaultPageSize = 50;

    private static readonly SearchValues<char> _likeMetacharacters = SearchValues.Create("\\%_");

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _investigationsTable;
    private readonly string _pinsTable;
    private readonly string _linksTable;

    public PostgresInvestigationStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _investigationsTable = SchemaSearchPath.QualifyTable("investigations", schemaName);
        _pinsTable = SchemaSearchPath.QualifyTable("investigation_pins", schemaName);
        _linksTable = SchemaSearchPath.QualifyTable("investigation_links", schemaName);
    }

    public async Task<InvestigationRecord> CreateAsync(
        string title,
        string createdBy,
        string? summary,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        var investigationId = GenerateInvestigationId();
        var sql = $"""
            INSERT INTO {_investigationsTable} (investigation_id, title, status, created_at, created_by, updated_at, summary)
            VALUES (@investigation_id, @title, @status, @created_at, @created_by, @updated_at, @summary)
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
            command.Parameters.AddWithValue("title", NpgsqlDbType.Text, title);
            command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, InvestigationStatusConversions.ToDbValue(InvestigationStatus.Open));
            command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, createdAt);
            command.Parameters.AddWithValue("created_by", NpgsqlDbType.Text, createdBy);
            command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, createdAt);
            command.Parameters.AddWithValue("summary", NpgsqlDbType.Text, (object?)summary ?? DBNull.Value);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new InvestigationRecord
        {
            InvestigationId = investigationId,
            Title = title,
            Status = InvestigationStatus.Open,
            CreatedAt = createdAt,
            CreatedBy = createdBy,
            UpdatedAt = createdAt,
            Summary = summary,
            Pins = ImmutableArray<InvestigationPin>.Empty,
            Links = ImmutableArray<InvestigationLink>.Empty
        };
    }

    public async Task<InvestigationRecord?> GetAsync(string investigationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(investigationId);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, transaction: null, investigationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvestigationPage> ListAsync(InvestigationFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var pageSize = ClampPageSize(filter.PageSize);
        var cursor = InvestigationCursor.TryDecode(filter.Cursor);

        var sql = new StringBuilder();
        sql.Append("SELECT i.investigation_id, i.title, i.status, i.created_at, i.created_by, i.updated_at, i.summary,");
        sql.Append(" (SELECT COUNT(*) FROM ").Append(_pinsTable).Append(" p WHERE p.investigation_id = i.investigation_id) AS pin_count,");
        sql.Append(" (SELECT COUNT(*) FROM ").Append(_linksTable).Append(" k WHERE k.investigation_id = i.investigation_id) AS link_count");
        sql.Append(" FROM ").Append(_investigationsTable).Append(" i WHERE 1=1");

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand { Connection = connection };

        if (filter.Status is { } status)
        {
            sql.Append(" AND i.status = @status");
            command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, InvestigationStatusConversions.ToDbValue(status));
        }

        if (!string.IsNullOrWhiteSpace(filter.CreatedBy))
        {
            sql.Append(" AND i.created_by = @created_by");
            command.Parameters.AddWithValue("created_by", NpgsqlDbType.Text, filter.CreatedBy);
        }

        if (!string.IsNullOrWhiteSpace(filter.TitleContains))
        {
            sql.Append(@" AND i.title ILIKE @title_contains ESCAPE '\'");
            command.Parameters.AddWithValue("title_contains", NpgsqlDbType.Text, "%" + EscapeLikePattern(filter.TitleContains) + "%");
        }

        if (cursor is not null)
        {
            sql.Append(" AND (i.updated_at, i.investigation_id) < (@cursor_updated_at, @cursor_id)");
            command.Parameters.AddWithValue("cursor_updated_at", NpgsqlDbType.TimestampTz, cursor.UpdatedAt);
            command.Parameters.AddWithValue("cursor_id", NpgsqlDbType.Text, cursor.InvestigationId);
        }

        sql.Append(" ORDER BY i.updated_at DESC, i.investigation_id DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);
        command.CommandText = sql.ToString();

        var rows = new List<InvestigationSummary>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new InvestigationSummary
            {
                InvestigationId = reader.GetString(0),
                Title = reader.GetString(1),
                Status = InvestigationStatusConversions.ToStatus(reader.GetInt16(2)),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                CreatedBy = reader.GetString(4),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                Summary = reader.IsDBNull(6) ? null : reader.GetString(6),
                PinCount = checked((int)reader.GetInt64(7)),
                LinkCount = checked((int)reader.GetInt64(8))
            });
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var lastKept = rows[pageSize - 1];
            rows.RemoveAt(rows.Count - 1);
            nextCursor = InvestigationCursor.Encode(lastKept.UpdatedAt, lastKept.InvestigationId);
        }

        return new InvestigationPage { Items = rows, NextCursor = nextCursor };
    }

    public async Task<InvestigationRecord?> UpdateAsync(
        string investigationId,
        string? title,
        string? summary,
        InvestigationStatus? status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(investigationId);

        if (title is null && summary is null && status is null)
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await LoadAsync(connection, transaction: null, investigationId, cancellationToken).ConfigureAwait(false);
        }

        var sql = new StringBuilder("UPDATE ").Append(_investigationsTable).Append(" SET updated_at = @updated_at");
        await using var connection2 = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand { Connection = connection2 };
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, updatedAt);

        if (title is not null)
        {
            sql.Append(", title = @title");
            command.Parameters.AddWithValue("title", NpgsqlDbType.Text, title);
        }

        if (summary is not null)
        {
            sql.Append(", summary = @summary");
            command.Parameters.AddWithValue("summary", NpgsqlDbType.Text, summary);
        }

        if (status is { } s)
        {
            sql.Append(", status = @status");
            command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, InvestigationStatusConversions.ToDbValue(s));
        }

        sql.Append(" WHERE investigation_id = @investigation_id");
        command.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
        command.CommandText = sql.ToString();
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            return null;
        }

        return await LoadAsync(connection2, transaction: null, investigationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvestigationRecord?> AddPinAsync(
        string investigationId,
        string eventRef,
        OperateEventKind eventKind,
        DateTimeOffset occurredAt,
        string? note,
        string actor,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(investigationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await ExistsAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var insertSql = $"""
            INSERT INTO {_pinsTable} (investigation_id, event_ref, event_kind, occurred_at, note, created_at, created_by)
            VALUES (@investigation_id, @event_ref, @event_kind, @occurred_at, @note, @created_at, @created_by)
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
            insert.Parameters.AddWithValue("event_ref", NpgsqlDbType.Text, eventRef);
            insert.Parameters.AddWithValue("event_kind", NpgsqlDbType.Smallint, (short)eventKind);
            insert.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, occurredAt);
            insert.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
            insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, createdAt);
            insert.Parameters.AddWithValue("created_by", NpgsqlDbType.Text, actor);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TouchAsync(connection, transaction, investigationId, createdAt, cancellationToken).ConfigureAwait(false);
        var record = await LoadAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<InvestigationRecord?> RemovePinAsync(
        string investigationId,
        long pinId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(investigationId);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await ExistsAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var deleteSql = $"""
            DELETE FROM {_pinsTable}
            WHERE investigation_id = @investigation_id AND pin_id = @pin_id
            """;
        int deleted;
        await using (var delete = new NpgsqlCommand(deleteSql, connection, transaction))
        {
            delete.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
            delete.Parameters.AddWithValue("pin_id", NpgsqlDbType.Bigint, pinId);
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (deleted == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await GetAsync(investigationId, cancellationToken).ConfigureAwait(false);
        }

        await TouchAsync(connection, transaction, investigationId, updatedAt, cancellationToken).ConfigureAwait(false);
        var record = await LoadAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<InvestigationRecord?> AddLinkAsync(
        string investigationId,
        InvestigationResourceKind resourceKind,
        string resourceId,
        string? note,
        string actor,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(investigationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await ExistsAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var insertSql = $"""
            INSERT INTO {_linksTable} (investigation_id, resource_kind, resource_id, note, created_at, created_by)
            VALUES (@investigation_id, @resource_kind, @resource_id, @note, @created_at, @created_by)
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
            insert.Parameters.AddWithValue("resource_kind", NpgsqlDbType.Smallint, (short)resourceKind);
            insert.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, resourceId);
            insert.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
            insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, createdAt);
            insert.Parameters.AddWithValue("created_by", NpgsqlDbType.Text, actor);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TouchAsync(connection, transaction, investigationId, createdAt, cancellationToken).ConfigureAwait(false);
        var record = await LoadAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<InvestigationRecord?> RemoveLinkAsync(
        string investigationId,
        long linkId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(investigationId);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await ExistsAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var deleteSql = $"""
            DELETE FROM {_linksTable}
            WHERE investigation_id = @investigation_id AND link_id = @link_id
            """;
        int deleted;
        await using (var delete = new NpgsqlCommand(deleteSql, connection, transaction))
        {
            delete.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
            delete.Parameters.AddWithValue("link_id", NpgsqlDbType.Bigint, linkId);
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (deleted == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await GetAsync(investigationId, cancellationToken).ConfigureAwait(false);
        }

        await TouchAsync(connection, transaction, investigationId, updatedAt, cancellationToken).ConfigureAwait(false);
        var record = await LoadAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    private async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string investigationId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT 1 FROM {_investigationsTable} WHERE investigation_id = @investigation_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private async Task TouchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string investigationId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var sql = $"UPDATE {_investigationsTable} SET updated_at = @updated_at WHERE investigation_id = @investigation_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, updatedAt);
        command.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<InvestigationRecord?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string investigationId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT investigation_id, title, status, created_at, created_by, updated_at, summary
            FROM {_investigationsTable} WHERE investigation_id = @investigation_id
            """;

        InvestigationRecord? record;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            record = new InvestigationRecord
            {
                InvestigationId = reader.GetString(0),
                Title = reader.GetString(1),
                Status = InvestigationStatusConversions.ToStatus(reader.GetInt16(2)),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                CreatedBy = reader.GetString(4),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                Summary = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
        }

        var pins = await LoadPinsAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false);
        var links = await LoadLinksAsync(connection, transaction, investigationId, cancellationToken).ConfigureAwait(false);
        return record with { Pins = pins, Links = links };
    }

    private async Task<ImmutableArray<InvestigationPin>> LoadPinsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string investigationId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT pin_id, event_ref, event_kind, occurred_at, note, created_at, created_by
            FROM {_pinsTable}
            WHERE investigation_id = @investigation_id
            ORDER BY pin_id
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);

        var builder = ImmutableArray.CreateBuilder<InvestigationPin>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            builder.Add(new InvestigationPin
            {
                PinId = reader.GetInt64(0),
                EventRef = reader.GetString(1),
                EventKind = (OperateEventKind)reader.GetInt16(2),
                OccurredAt = reader.GetFieldValue<DateTimeOffset>(3),
                Note = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                CreatedBy = reader.GetString(6)
            });
        }

        return builder.ToImmutable();
    }

    private async Task<ImmutableArray<InvestigationLink>> LoadLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string investigationId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT link_id, resource_kind, resource_id, note, created_at, created_by
            FROM {_linksTable}
            WHERE investigation_id = @investigation_id
            ORDER BY link_id
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("investigation_id", NpgsqlDbType.Text, investigationId);

        var builder = ImmutableArray.CreateBuilder<InvestigationLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            builder.Add(new InvestigationLink
            {
                LinkId = reader.GetInt64(0),
                ResourceKind = (InvestigationResourceKind)reader.GetInt16(1),
                ResourceId = reader.GetString(2),
                Note = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
                CreatedBy = reader.GetString(5)
            });
        }

        return builder.ToImmutable();
    }

    internal static int ClampPageSize(int requested)
    {
        if (requested <= 0)
        {
            return DefaultPageSize;
        }

        return Math.Min(MaxPageSize, Math.Max(MinPageSize, requested));
    }

    internal static string EscapeLikePattern(string value)
    {
        if (value.AsSpan().IndexOfAny(_likeMetacharacters) < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            if (ch is '\\' or '%' or '_')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    internal static string GenerateInvestigationId()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var encoded = Base32.Encode(bytes);
        return "inv_" + encoded;
    }
}

internal static class InvestigationStatusConversions
{
    public static InvestigationStatus ToStatus(short value) => value switch
    {
        0 => InvestigationStatus.Open,
        1 => InvestigationStatus.Closed,
        _ => throw new InvalidOperationException($"Unsupported investigation status '{value}'.")
    };

    public static short ToDbValue(InvestigationStatus value) => value switch
    {
        InvestigationStatus.Open => 0,
        InvestigationStatus.Closed => 1,
        _ => throw new InvalidOperationException($"Unsupported investigation status '{value}'.")
    };
}

internal sealed record InvestigationCursor(DateTimeOffset UpdatedAt, string InvestigationId)
{
    public static string Encode(DateTimeOffset updatedAt, string investigationId)
        => Base64Url.Encode($"{updatedAt.UtcTicks}:{investigationId}");

    public static InvestigationCursor? TryDecode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        if (!Base64Url.TryDecode(cursor, out var decoded))
        {
            return null;
        }

        var separator = decoded.IndexOf(':');
        if (separator <= 0 || separator == decoded.Length - 1)
        {
            return null;
        }

        if (!long.TryParse(decoded.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            return null;
        }

        var id = decoded[(separator + 1)..];
        return new InvestigationCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}

internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                int index = (buffer >> (bitsLeft - 5)) & 0x1F;
                bitsLeft -= 5;
                sb.Append(Alphabet[index]);
            }
        }

        if (bitsLeft > 0)
        {
            int index = (buffer << (5 - bitsLeft)) & 0x1F;
            sb.Append(Alphabet[index]);
        }

        return sb.ToString();
    }
}
