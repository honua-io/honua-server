// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// PostgreSQL implementation of <see cref="IMigrationPerformanceEvidenceStore"/> backed
/// by <c>honua.migration_performance_evidence</c> (#1033 slice 5).
/// </summary>
/// <remarks>
/// The artifact JSON is round-tripped through a caller-supplied
/// <see cref="JsonTypeInfo{T}"/> so the implementation stays AOT-safe and works whether
/// the Server's source-generated JSON context or a fallback reflection context is
/// supplied at registration time.
/// </remarks>
internal sealed class PostgresMigrationPerformanceEvidenceStore : IMigrationPerformanceEvidenceStore
{
    private const int MaxLimit = 200;
    private const int DefaultLimit = 25;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly JsonTypeInfo<MigrationPerformanceEvidenceArtifact> _artifactTypeInfo;

    public PostgresMigrationPerformanceEvidenceStore(
        IDatabaseConnectionProvider connectionProvider,
        JsonTypeInfo<MigrationPerformanceEvidenceArtifact> artifactTypeInfo)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _artifactTypeInfo = artifactTypeInfo ?? throw new ArgumentNullException(nameof(artifactTypeInfo));
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(MigrationPerformanceEvidenceRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EvidenceId);

        const string sql = """
            INSERT INTO honua.migration_performance_evidence (
                evidence_id, source_family, fixture_size, status, generated_at, fingerprint, artifact_json
            ) VALUES (
                @evidence_id, @source_family, @fixture_size, @status, @generated_at, @fingerprint, @artifact_json::jsonb
            )
            ON CONFLICT (evidence_id) DO UPDATE SET
                source_family = EXCLUDED.source_family,
                fixture_size  = EXCLUDED.fixture_size,
                status        = EXCLUDED.status,
                generated_at  = EXCLUDED.generated_at,
                fingerprint   = EXCLUDED.fingerprint,
                artifact_json = EXCLUDED.artifact_json
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("evidence_id", NpgsqlDbType.Text, record.EvidenceId);
        command.Parameters.AddWithValue("source_family", NpgsqlDbType.Text, record.SourceFamily);
        command.Parameters.AddWithValue("fixture_size", NpgsqlDbType.Text, record.FixtureSize);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, record.Status);
        command.Parameters.AddWithValue("generated_at", NpgsqlDbType.TimestampTz, record.GeneratedAt);
        command.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Text, record.Fingerprint);
        command.Parameters.AddWithValue("artifact_json", NpgsqlDbType.Text, JsonSerializer.Serialize(record.Artifact, _artifactTypeInfo));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MigrationPerformanceEvidenceRecord?> GetLatestPassingAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT evidence_id, source_family, fixture_size, status, generated_at, fingerprint, artifact_json
            FROM honua.migration_performance_evidence
            WHERE status ILIKE 'Pass'
            ORDER BY generated_at DESC, evidence_id ASC
            LIMIT 1
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    /// <inheritdoc />
    public async ValueTask<MigrationPerformanceEvidenceRecord?> GetByIdAsync(
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);

        const string sql = """
            SELECT evidence_id, source_family, fixture_size, status, generated_at, fingerprint, artifact_json
            FROM honua.migration_performance_evidence
            WHERE evidence_id = @evidence_id
            LIMIT 1
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("evidence_id", NpgsqlDbType.Text, evidenceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MigrationPerformanceEvidenceRecord>> GetHistoryAsync(
        string? sourceFamily,
        string? fixtureSize,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var clampedLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

        const string sql = """
            SELECT evidence_id, source_family, fixture_size, status, generated_at, fingerprint, artifact_json
            FROM honua.migration_performance_evidence
            WHERE (@source_family IS NULL OR source_family ILIKE @source_family)
              AND (@fixture_size  IS NULL OR fixture_size  ILIKE @fixture_size)
            ORDER BY generated_at DESC, evidence_id ASC
            LIMIT @limit
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("source_family", NpgsqlDbType.Text, (object?)NormalizeFilter(sourceFamily) ?? DBNull.Value);
        command.Parameters.AddWithValue("fixture_size", NpgsqlDbType.Text, (object?)NormalizeFilter(fixtureSize) ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, clampedLimit);

        var results = new List<MigrationPerformanceEvidenceRecord>(capacity: clampedLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    private static string? NormalizeFilter(string? candidate)
        => string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();

    private MigrationPerformanceEvidenceRecord ReadRecord(NpgsqlDataReader reader)
    {
        var json = reader.GetString(6);
        var artifact = JsonSerializer.Deserialize(json, _artifactTypeInfo)
            ?? throw new InvalidOperationException("Stored migration performance evidence artifact failed to deserialize.");

        return new MigrationPerformanceEvidenceRecord
        {
            EvidenceId = reader.GetString(0),
            SourceFamily = reader.GetString(1),
            FixtureSize = reader.GetString(2),
            Status = reader.GetString(3),
            GeneratedAt = reader.GetFieldValue<DateTimeOffset>(4),
            Fingerprint = reader.GetString(5),
            Artifact = artifact,
        };
    }
}
