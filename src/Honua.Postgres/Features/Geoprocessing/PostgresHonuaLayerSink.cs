// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Geoprocessing;

/// <summary>
/// Catalog-database implementation of <see cref="IHonuaLayerSink"/>. Loads pre-encoded
/// feature rows into a named layer table in the Honua catalog using the catalog's own
/// <see cref="NpgsqlDataSource"/>. This type — not the geoprocessing dispatcher — owns the
/// dependency on the catalog data source, so it is registered only by the Postgres provider
/// and is simply absent in lean deployments (#2210).
/// </summary>
/// <remarks>
/// The load runs inside a single transaction so a failure leaves the destination table
/// untouched; on success every row carries the reserved <c>__pipeline_batch_id</c> key in
/// its attributes JSONB (supplied by the caller) so a completed load can be soft-deleted by
/// batch id. Identifiers are re-validated here as defense in depth even though the executor
/// already validates them, because they are interpolated into DDL/DML.
/// </remarks>
internal sealed partial class PostgresHonuaLayerSink(NpgsqlDataSource dataSource) : IHonuaLayerSink
{
    private const char KeyFieldSeparator = '\u001F';
    private const int InsertChunkSize = 5000;

    private readonly NpgsqlDataSource _dataSource = dataSource
        ?? throw new ArgumentNullException(nameof(dataSource));

    /// <inheritdoc />
    public async Task<HonuaLayerSinkOutcome> LoadAsync(
        HonuaLayerSinkRequest request,
        IReadOnlyList<HonuaLayerSinkRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);

        var schema = Identifier(request.Schema, nameof(request.Schema));
        var table = Identifier(request.Table, nameof(request.Table));
        var geometryColumn = Identifier(request.GeometryColumn, nameof(request.GeometryColumn));
        foreach (var key in request.KeyFields)
        {
            _ = Identifier(key, "keyField");
        }

        var srid = request.TargetSrid.ToString(CultureInfo.InvariantCulture);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await EnsureTableAsync(connection, transaction, schema, table, geometryColumn, srid, cancellationToken)
            .ConfigureAwait(false);

        switch (request.LoadMode)
        {
            case HonuaLayerLoadMode.Replace:
                await DeleteAllAsync(connection, transaction, schema, table, cancellationToken).ConfigureAwait(false);
                break;
            case HonuaLayerLoadMode.Upsert:
                await DeleteMatchingKeysAsync(
                    connection, transaction, schema, table, request.KeyFields, rows, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case HonuaLayerLoadMode.Append:
            default:
                break;
        }

        long written = 0;
        for (var offset = 0; offset < rows.Count; offset += InsertChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(InsertChunkSize, rows.Count - offset);
            written += await InsertChunkAsync(
                connection, transaction, schema, table, geometryColumn, srid, rows, offset, count, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new HonuaLayerSinkOutcome(written, schema, table, request.BatchId);
    }

    private static async Task EnsureTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        string geometryColumn,
        string srid,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            CREATE SCHEMA IF NOT EXISTS "{schema}";
            CREATE TABLE IF NOT EXISTS "{schema}"."{table}" (
                id          BIGSERIAL PRIMARY KEY,
                "{geometryColumn}" geometry(Geometry, {srid}),
                attributes  JSONB NOT NULL
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteAllAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"DELETE FROM \"{schema}\".\"{table}\"", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteMatchingKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        IReadOnlyList<string> keyFields,
        IReadOnlyList<HonuaLayerSinkRow> rows,
        CancellationToken cancellationToken)
    {
        var keys = BuildIncomingKeys(keyFields, rows);
        if (keys.Count == 0)
        {
            return;
        }

        // Compare a deterministic composite of the key fields' JSONB text values against the
        // incoming set, so a composite key is matched correctly with a single parameterized
        // ANY(...) rather than per-row dynamic SQL.
        var keyExpression = BuildKeyExpression(keyFields);
        var sql = $"DELETE FROM \"{schema}\".\"{table}\" WHERE {keyExpression} = ANY(@keys)";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add("keys", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = keys.ToArray();
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildKeyExpression(IReadOnlyList<string> keyFields)
    {
        // concat_ws(chr(31), attributes->>'k1', attributes->>'k2', ...)
        var builder = new StringBuilder("concat_ws(chr(31)");
        foreach (var field in keyFields)
        {
            builder.Append(", attributes->>'").Append(field).Append('\'');
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static HashSet<string> BuildIncomingKeys(
        IReadOnlyList<string> keyFields,
        IReadOnlyList<HonuaLayerSinkRow> rows)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row.AttributesJson);
            var root = document.RootElement;
            var builder = new StringBuilder();
            for (var i = 0; i < keyFields.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(KeyFieldSeparator);
                }

                if (root.TryGetProperty(keyFields[i], out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    builder.Append(value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : value.GetRawText());
                }
            }

            keys.Add(builder.ToString());
        }

        return keys;
    }

    private static async Task<long> InsertChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        string geometryColumn,
        string srid,
        IReadOnlyList<HonuaLayerSinkRow> rows,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var wkbs = new byte[count][];
        var attributes = new string[count];
        for (var i = 0; i < count; i++)
        {
            wkbs[i] = rows[offset + i].WellKnownBinary;
            attributes[i] = rows[offset + i].AttributesJson;
        }

        var sql = $"""
            INSERT INTO "{schema}"."{table}" ("{geometryColumn}", attributes)
            SELECT ST_SetSRID(ST_GeomFromWKB(payload.wkb), {srid}), payload.attributes
            FROM unnest(@wkbs, @attributes) AS payload(wkb, attributes)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add("wkbs", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = wkbs;
        command.Parameters.Add("attributes", NpgsqlDbType.Array | NpgsqlDbType.Jsonb).Value = attributes;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Identifier(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value))
        {
            throw new ArgumentException(
                $"{role} identifier is invalid; identifiers must match ^[A-Za-z_][A-Za-z0-9_]*$.",
                nameof(value));
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();
}
