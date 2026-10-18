// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Styling.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Styling;

internal sealed partial class PostgresStyleCatalog
{
    public Task<StyleEditStoreSnapshot> GetEditSnapshotAsync(string styleId, int? canonicalLayerId,
        CancellationToken cancellationToken = default)
        => GetEditSnapshotForReferencesAsync(styleId, canonicalLayerId, [styleId], cancellationToken);

    public async Task<StyleEditStoreSnapshot> GetEditSnapshotForReferencesAsync(string styleId, int? canonicalLayerId,
        IReadOnlyList<string> referenceIds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var result = await ReadEditSnapshotAsync(connection, transaction, styleId, canonicalLayerId, false, cancellationToken).ConfigureAwait(false);
        result = await ReadConsumersAsync(connection, transaction, result, referenceIds, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<StyleEditStoreSnapshot?> ApplyEditIfSnapshotAsync(StyleEditStoreSnapshot expected,
        string mapLibreStyleJson, string drawingInfoJson, string? revisedBy,
        Func<CancellationToken, Task> revalidate, CancellationToken cancellationToken = default)
    {
        var outcome = await ApplyEditCoreAsync(expected, mapLibreStyleJson, drawingInfoJson, revisedBy,
            revalidate, null, null, cancellationToken).ConfigureAwait(false);
        return outcome?.Snapshot;
    }

    public async Task<StyleMutationOutcome?> ApplyEditOnceAsync(StyleEditStoreSnapshot expected, string mapLibreStyleJson,
        string drawingInfoJson, string? revisedBy, StyleMutationIdentity identity,
        Func<CancellationToken, Task> revalidateMutation, Func<CancellationToken, Task> revalidateReceipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revalidateMutation);
        ArgumentNullException.ThrowIfNull(revalidateReceipt);
        ValidateIdentity(identity);
        using var candidateDocument = System.Text.Json.JsonDocument.Parse(mapLibreStyleJson);
        var candidateCanonical = Honua.Core.Features.Collaboration.Operations.SavedMapOperationIdempotency.CanonicalPayload(candidateDocument.RootElement);
        var candidateHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidateCanonical)));
        if (!string.Equals(candidateHash, identity.CandidateHash, StringComparison.Ordinal))
            throw new ArgumentException("Candidate bytes do not match the sealed mutation identity.", nameof(identity));
        var outcome = await ApplyEditCoreAsync(expected, mapLibreStyleJson, drawingInfoJson, revisedBy,
            revalidateMutation, identity, revalidateReceipt, cancellationToken).ConfigureAwait(false);
        return outcome is null ? null : new(outcome.Value.Receipt!, outcome.Value.Snapshot);
    }

    private async Task<(StyleEditStoreSnapshot? Snapshot, StyleMutationReceipt? Receipt)?> ApplyEditCoreAsync(
        StyleEditStoreSnapshot expected, string mapLibreStyleJson, string drawingInfoJson, string? revisedBy,
        Func<CancellationToken, Task> revalidate, StyleMutationIdentity? identity,
        Func<CancellationToken, Task>? revalidateReceipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(revalidate);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(bounded.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(bounded.Token).ConfigureAwait(false);
        await Infrastructure.StudioStyleDependencyBarrier.AcquireAsync(connection, transaction, _styleImpactBarrierKey, true, bounded.Token).ConfigureAwait(false);
        if (identity is not null)
        {
            var receipt = await ReadMutationReceiptAsync(connection, transaction, identity, bounded.Token).ConfigureAwait(false);
            if (receipt is not null)
            {
                await revalidateReceipt!(bounded.Token).ConfigureAwait(false);
                return (null, receipt);
            }
            if (!string.Equals(identity.StyleId, expected.StyleId, StringComparison.Ordinal))
                throw new InvalidOperationException("Style mutation identity does not match the canonical target.");
        }
        var current = await ReadEditSnapshotAsync(connection, transaction, expected.StyleId,
            expected.LayerStyle?.LayerId, true, bounded.Token).ConfigureAwait(false);
        current = await ReadConsumersAsync(connection, transaction, current, expected.ReferenceIds, bounded.Token).ConfigureAwait(false);
        if (!SameSnapshot(expected, current)) return null;
        // This callback rechecks live key, membership, tenant, frozen turn and every
        // affected resource after the lock wait, before changing any canonical bytes.
        await revalidate(bounded.Token).ConfigureAwait(false);
        if (current.LayerStyle is { } layer)
        {
            var updateLayer = $"""
                UPDATE {_layersTable} SET maplibre_style = @mapLibreStyle,
                  geoservices_drawing_info = @drawingInfo, style_version = COALESCE(style_version, 0) + 1,
                  style_revised_at = NOW(), style_revised_by = @revisedBy, style_change_summary = NULL
                WHERE layer_id = @layerId AND COALESCE(style_version, 0) = @expectedVersion
                """;
            await using var command = new NpgsqlCommand(updateLayer, connection, transaction);
            command.Parameters.AddWithValue("layerId", layer.LayerId);
            command.Parameters.AddWithValue("expectedVersion", layer.StyleVersion);
            AddJsonb(command, "mapLibreStyle", mapLibreStyleJson);
            AddJsonb(command, "drawingInfo", drawingInfoJson);
            AddNullableText(command, "revisedBy", revisedBy);
            if (await command.ExecuteNonQueryAsync(bounded.Token).ConfigureAwait(false) != 1) return null;
        }
        var catalogSql = current.CatalogStyle is null
            ? $"""
                INSERT INTO {_stylesTable} (style_id, maplibre_style, drawing_info, style_version, created_at, updated_at, revised_by)
                VALUES (@styleId, @mapLibreStyle, @drawingInfo, 1, NOW(), NOW(), @revisedBy)
                ON CONFLICT (style_id) DO NOTHING
                """
            : $"""
                UPDATE {_stylesTable} SET maplibre_style = @mapLibreStyle, drawing_info = @drawingInfo,
                  style_version = style_version + 1, updated_at = NOW(), revised_by = @revisedBy, change_summary = NULL
                WHERE style_id = @styleId AND style_version = @expectedVersion
                """;
        await using (var command = new NpgsqlCommand(catalogSql, connection, transaction))
        {
            command.Parameters.AddWithValue("styleId", expected.StyleId);
            if (current.CatalogStyle is { } style) command.Parameters.AddWithValue("expectedVersion", style.StyleVersion);
            AddJsonb(command, "mapLibreStyle", mapLibreStyleJson);
            AddJsonb(command, "drawingInfo", drawingInfoJson);
            AddNullableText(command, "revisedBy", revisedBy);
            if (await command.ExecuteNonQueryAsync(bounded.Token).ConfigureAwait(false) != 1) return null;
        }
        if (current.LayerStyle is { } mirrored)
        {
            // Same layer-owned primary mirror semantics as LayerStyleService, now in
            // the canonical transaction with its bytes and independently versioned catalog row.
            var associateSql = $"""
                INSERT INTO {_refsTable} (layer_id, style_id, ordinal) VALUES (@layerId, @styleId, 0)
                ON CONFLICT (layer_id, style_id) DO UPDATE SET ordinal = 0;
                WITH last_alternate AS (SELECT COALESCE(MAX(ordinal), 0) AS ordinal FROM {_refsTable} WHERE layer_id = @layerId),
                demoted AS (SELECT style_id, (SELECT ordinal FROM last_alternate) + ROW_NUMBER() OVER (ORDER BY style_id)::integer AS new_ordinal
                  FROM {_refsTable} WHERE layer_id = @layerId AND style_id <> @styleId AND ordinal = 0)
                UPDATE {_refsTable} AS refs SET ordinal = demoted.new_ordinal FROM demoted
                  WHERE refs.layer_id = @layerId AND refs.style_id = demoted.style_id
                """;
            await using var command = new NpgsqlCommand(associateSql, connection, transaction);
            command.Parameters.AddWithValue("layerId", mirrored.LayerId);
            command.Parameters.AddWithValue("styleId", expected.StyleId);
            await command.ExecuteNonQueryAsync(bounded.Token).ConfigureAwait(false);
        }
        var updated = await ReadEditSnapshotAsync(connection, transaction, expected.StyleId,
            expected.LayerStyle?.LayerId, false, bounded.Token).ConfigureAwait(false);
        updated = await ReadConsumersAsync(connection, transaction, updated, expected.ReferenceIds, bounded.Token).ConfigureAwait(false);
        StyleMutationReceipt? committedReceipt = null;
        if (identity is not null)
            committedReceipt = await InsertMutationReceiptAsync(connection, transaction, identity,
                updated.LayerStyle?.StyleVersion ?? updated.CatalogStyle!.StyleVersion, bounded.Token).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(bounded.Token).ConfigureAwait(false);
        return (updated, committedReceipt);
    }

    private static bool SameSnapshot(StyleEditStoreSnapshot expected, StyleEditStoreSnapshot current)
        => expected.StyleId == current.StyleId &&
           expected.ConsumerIndexComplete && current.ConsumerIndexComplete &&
           expected.ReferenceIds.SequenceEqual(current.ReferenceIds) &&
           expected.Consumers.SequenceEqual(current.Consumers) &&
           expected.CatalogStyle?.StyleVersion == current.CatalogStyle?.StyleVersion &&
           expected.CatalogStyle?.CreatedAt == current.CatalogStyle?.CreatedAt &&
           expected.CatalogStyle?.MapLibreStyleJson == current.CatalogStyle?.MapLibreStyleJson &&
           expected.LayerStyle?.LayerId == current.LayerStyle?.LayerId &&
           expected.LayerStyle?.StyleVersion == current.LayerStyle?.StyleVersion &&
           expected.LayerStyle?.StyleRevisedAt == current.LayerStyle?.StyleRevisedAt &&
           expected.LayerStyle?.MapLibreStyleJson == current.LayerStyle?.MapLibreStyleJson &&
           expected.Associations.OrderBy(item => item.LayerId).ThenBy(item => item.Ordinal)
               .SequenceEqual(current.Associations.OrderBy(item => item.LayerId).ThenBy(item => item.Ordinal));

    private async Task<StyleEditStoreSnapshot> ReadEditSnapshotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string styleId, int? layerId, bool locking, CancellationToken cancellationToken)
    {
        // Follow association writers' layer-before-style lock order. The read-only
        // caller uses RepeatableRead; mutators retain row locks through commit.
        LayerStyleDefinition? layer = null;
        if (layerId is { } id)
        {
            var sql = $"SELECT layer_id, maplibre_style::text, geoservices_drawing_info::text, COALESCE(style_version, 0), style_revised_at, style_revised_by, style_change_summary FROM {_layersTable} WHERE layer_id = @layerId" + (locking ? " FOR UPDATE" : "");
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("layerId", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) layer = new()
            {
                LayerId = reader.GetInt32(0), MapLibreStyleJson = reader.IsDBNull(1) ? null : reader.GetString(1),
                DrawingInfoJson = reader.IsDBNull(2) ? null : reader.GetString(2), StyleVersion = reader.GetInt32(3),
                StyleRevisedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                StyleRevisedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                StyleChangeSummary = reader.IsDBNull(6) ? null : reader.GetString(6),
            };
        }
        StyleCatalogRecord? style = null;
        await using (var command = new NpgsqlCommand($"SELECT {SelectColumns} FROM {_stylesTable} WHERE style_id = @styleId" + (locking ? " FOR UPDATE" : ""), connection, transaction))
        {
            command.Parameters.AddWithValue("styleId", styleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) style = ReadStyle(reader);
        }
        List<StyleLayerAssociation> associations = [];
        await using (var command = new NpgsqlCommand($"SELECT layer_id, style_id, ordinal FROM {_refsTable} WHERE style_id = @styleId ORDER BY layer_id, ordinal" + (locking ? " FOR SHARE" : ""), connection, transaction))
        {
            command.Parameters.AddWithValue("styleId", styleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) associations.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return new(styleId, style, layer, associations);
    }
}
