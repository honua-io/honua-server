// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// PostgreSQL-backed immutable store for migration evidence reports.
/// </summary>
internal sealed class PostgresMigrationEvidenceReportStore : IMigrationEvidenceReportStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresMigrationEvidenceReportStore(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _table = Infrastructure.SchemaSearchPath.QualifyTable("migration_evidence_reports", schemaName);
    }

    public async Task StoreAsync(MigrationEvidenceReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        const string sqlTemplate = """
            INSERT INTO {0}
            (
                report_id,
                provider,
                cutover_profile,
                readiness,
                source_service_url,
                target_base_url,
                target_service_name,
                inventory_artifact_ref,
                translation_manifest_ref,
                import_job_id,
                report_hash,
                generated_by,
                generated_at,
                warnings_count,
                blockers_count,
                artifact
            )
            VALUES
            (
                @reportId,
                @provider,
                @cutoverProfile,
                @readiness,
                @sourceServiceUrl,
                @targetBaseUrl,
                @targetServiceName,
                @inventoryArtifactRef,
                @translationManifestRef,
                @importJobId,
                @reportHash,
                @generatedBy,
                @generatedAt,
                @warningsCount,
                @blockersCount,
                @artifact
            )
            """;

        var sql = string.Format(sqlTemplate, _table);
        var artifactJson = JsonSerializer.Serialize(
            report,
            MigrationEvidenceDomainJsonContext.Default.MigrationEvidenceReport);

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@reportId", report.ReportId);
        command.Parameters.AddWithValue("@provider", report.Request.Provider.ToString());
        command.Parameters.AddWithValue("@cutoverProfile", report.Request.CutoverProfile.ToString());
        command.Parameters.AddWithValue("@readiness", report.CutoverReadiness.State.ToString());
        command.Parameters.AddWithValue("@sourceServiceUrl", (object?)report.Request.SourceServiceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@targetBaseUrl", report.Request.TargetBaseUrl);
        command.Parameters.AddWithValue("@targetServiceName", report.Request.TargetServiceName);
        command.Parameters.AddWithValue("@inventoryArtifactRef", (object?)report.Request.InventoryArtifactRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@translationManifestRef", (object?)report.Request.TranslationManifestRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@importJobId", (object?)report.Request.ImportJobId ?? DBNull.Value);
        command.Parameters.AddWithValue("@reportHash", report.ReportHash);
        command.Parameters.AddWithValue("@generatedBy", (object?)report.Request.RequestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@generatedAt", report.GeneratedAt);
        command.Parameters.AddWithValue("@warningsCount", report.CutoverReadiness.Warnings.Length);
        command.Parameters.AddWithValue("@blockersCount", report.CutoverReadiness.BlockingReasons.Length);
        command.Parameters.Add(new NpgsqlParameter("@artifact", NpgsqlDbType.Jsonb)
        {
            Value = artifactJson
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MigrationEvidenceReport?> GetAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        const string sqlTemplate = """
            SELECT artifact
            FROM {0}
            WHERE report_id = @reportId
            """;

        var sql = string.Format(sqlTemplate, _table);

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@reportId", reportId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not string artifactJson)
        {
            return null;
        }

        return JsonSerializer.Deserialize(
            artifactJson,
            MigrationEvidenceDomainJsonContext.Default.MigrationEvidenceReport);
    }

    public async Task<IReadOnlyList<MigrationEvidenceReportSummary>> ListAsync(
        int limit = 50,
        int offset = 0,
        MigrationEvidenceProvider? provider = null,
        MigrationCutoverProfile? cutoverProfile = null,
        MigrationReadinessState? readiness = null,
        CancellationToken cancellationToken = default)
    {
        var filters = new List<string>();
        if (provider.HasValue)
        {
            filters.Add("provider = @provider");
        }

        if (cutoverProfile.HasValue)
        {
            filters.Add("cutover_profile = @cutoverProfile");
        }

        if (readiness.HasValue)
        {
            filters.Add("readiness = @readiness");
        }

        var whereClause = filters.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", filters)}";

        var sql = $@"
            SELECT
                report_id,
                artifact ->> 'schemaVersion' AS schema_version,
                provider,
                cutover_profile,
                readiness,
                source_service_url,
                target_base_url,
                target_service_name,
                report_hash,
                generated_by,
                artifact #>> '{{request,summary}}' AS summary,
                inventory_artifact_ref,
                translation_manifest_ref,
                import_job_id,
                warnings_count,
                blockers_count,
                generated_at
            FROM {_table}
            {whereClause}
            ORDER BY generated_at DESC
            LIMIT @limit OFFSET @offset";

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 0, 200));
        command.Parameters.AddWithValue("@offset", Math.Max(0, offset));

        if (provider.HasValue)
        {
            command.Parameters.AddWithValue("@provider", provider.Value.ToString());
        }

        if (cutoverProfile.HasValue)
        {
            command.Parameters.AddWithValue("@cutoverProfile", cutoverProfile.Value.ToString());
        }

        if (readiness.HasValue)
        {
            command.Parameters.AddWithValue("@readiness", readiness.Value.ToString());
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<MigrationEvidenceReportSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new MigrationEvidenceReportSummary
            {
                ReportId = reader.GetGuid(0),
                SchemaVersion = reader.IsDBNull(1) ? "migration-evidence/v1" : reader.GetString(1),
                Provider = ParseEnum<MigrationEvidenceProvider>(reader.GetString(2)),
                CutoverProfile = ParseEnum<MigrationCutoverProfile>(reader.GetString(3)),
                Readiness = ParseEnum<MigrationReadinessState>(reader.GetString(4)),
                SourceServiceUrl = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                TargetBaseUrl = reader.GetString(6),
                TargetServiceName = reader.GetString(7),
                ReportHash = reader.GetString(8),
                RequestedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
                Summary = reader.IsDBNull(10) ? null : reader.GetString(10),
                InventoryArtifactRef = reader.IsDBNull(11) ? null : reader.GetString(11),
                TranslationManifestRef = reader.IsDBNull(12) ? null : reader.GetString(12),
                ImportJobId = reader.IsDBNull(13) ? null : reader.GetString(13),
                WarningCount = reader.GetInt32(14),
                BlockerCount = reader.GetInt32(15),
                GeneratedAt = reader.GetFieldValue<DateTimeOffset>(16)
            });
        }

        return results;
    }

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unable to parse '{value}' as {typeof(TEnum).Name}.");
    }
}
