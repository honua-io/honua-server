// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Npgsql;
using NpgsqlTypes;

using Honua.Db.Postgres.Features.Infrastructure;
namespace Honua.Db.Postgres.Features.Geoprocessing;

/// <summary>
/// Catalog-database implementation of <see cref="IHonuaLayerSink"/>. Loads pre-encoded
/// feature rows into a named layer table in the Honua catalog using the catalog's own
/// <see cref="NpgsqlDataSource"/>. This type â€” not the geoprocessing dispatcher â€” owns the
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
/// <remarks>
/// <para>
/// <b>Repeat-safety (server#4626):</b> a commit receipt keyed on
/// (<c>table_name</c>, <c>batch_id</c>) is written in the <em>same</em> transaction as the
/// data rows, in a reserved <c>__honua_layer_sink_receipts</c> table living alongside the
/// destination table. Because the receipt and the data mutation share one atomic commit,
/// there is no window in which the data can be durably committed while the receipt is not
/// (or vice versa) — the two states can never observably diverge. A crash between this
/// method returning and the job being marked terminal (including a hard process kill, which
/// no cancellation token observes) therefore leaves a durable, checkable fact behind: a
/// retry that replays the same request re-opens a transaction, finds the existing receipt
/// before touching any row, and returns the receipt's reconstructed outcome without
/// repeating the append/replace/upsert effect or performing a second data write.
/// </para>
/// </remarks>
internal sealed partial class PostgresHonuaLayerSink(NpgsqlDataSource dataSource) : IHonuaLayerSink
{
    private const char KeyFieldSeparator = '\u001F';
    private const int InsertChunkSize = 5000;
    private const string ReceiptsTableName = "__honua_layer_sink_receipts";

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

        if (string.IsNullOrWhiteSpace(request.BatchId))
        {
            throw new ArgumentException("BatchId is required for a repeat-safe commit receipt.", nameof(request));
        }

        var srid = request.TargetSrid.ToString(CultureInfo.InvariantCulture);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await EnsureTableAsync(connection, transaction, schema, table, geometryColumn, srid, cancellationToken)
            .ConfigureAwait(false);
        await EnsureReceiptsTableAsync(connection, transaction, schema, cancellationToken).ConfigureAwait(false);

        // Resolve ambiguous commit outcomes using the durable receipt rather than blindly
        // repeating an operation whose prior commit status is unknown: a replay of the same
        // (table, batchId) short-circuits here without touching any destination row.
        var existingReceipt = await TryReadReceiptAsync(
            connection, transaction, schema, table, request.BatchId, cancellationToken).ConfigureAwait(false);
        if (existingReceipt is { } replay)
        {
            // Nothing to write on this attempt; commit (not roll back) so the read-only
            // transaction closes cleanly instead of lingering open on the connection.
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

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

        // Inserted — and committed — in the same transaction as the data rows above, so the
        // two effects are atomic: a durable receipt for this batchId exists if and only if
        // the corresponding rows were durably committed.
        await InsertReceiptAsync(
            connection, transaction, schema, table, request.BatchId, request.LoadMode, written, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

        return new HonuaLayerSinkOutcome(written, schema, table, request.BatchId);
    }

    private static async Task EnsureReceiptsTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            CREATE TABLE IF NOT EXISTS "{schema}"."{ReceiptsTableName}" (
                table_name        text NOT NULL,
                batch_id          text NOT NULL,
                load_mode         text NOT NULL,
                features_written  bigint NOT NULL,
                committed_at      timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (table_name, batch_id)
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HonuaLayerSinkOutcome?> TryReadReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        string batchId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT features_written FROM "{schema}"."{ReceiptsTableName}"
            WHERE table_name = @table AND batch_id = @batchId
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("batchId", batchId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return null;
        }

        return new HonuaLayerSinkOutcome((long)result, schema, table, batchId);
    }

    private static async Task InsertReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        string batchId,
        HonuaLayerLoadMode loadMode,
        long written,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO "{schema}"."{ReceiptsTableName}" (table_name, batch_id, load_mode, features_written)
            VALUES (@table, @batchId, @loadMode, @written)
            ON CONFLICT (table_name, batch_id) DO NOTHING
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("batchId", batchId);
        command.Parameters.AddWithValue("loadMode", loadMode.ToString());
        command.Parameters.AddWithValue("written", written);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        // Keep each projected document scoped to one iteration while reusing the key builder.
        var builder = new StringBuilder();
        foreach (var document in rows.Select(row => JsonDocument.Parse(row.AttributesJson)))
        {
            using (document)
            {
                var root = document.RootElement;
                builder.Clear();
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
