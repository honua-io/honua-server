// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// PostgreSQL-backed <see cref="IArcGisMigrationEvidenceStore"/>. Persists manifest and parity
/// artifacts as JSONB so schema evolution is owned by the artifact records themselves
/// (#1025 slice 6).
/// </summary>
internal sealed class PostgresArcGisMigrationEvidenceStore : IArcGisMigrationEvidenceStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresArcGisMigrationEvidenceStore(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _table = SchemaSearchPath.QualifyTable("arcgis_migration_runs", schemaName);
    }

    public async Task SaveManifestAsync(
        ArcGisMigrationRunRecord record,
        MigrationManifestArtifact manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);
        ArgumentNullException.ThrowIfNull(manifest);

        var sql = $"""
            INSERT INTO {_table} (
                run_id, source_url, source_display_name, source_version, actor,
                created_at, target_resource_count, manifest_json
            ) VALUES (
                @run_id, @source_url, @source_display_name, @source_version, @actor,
                @created_at, @target_resource_count, @manifest_json
            )
            ON CONFLICT (run_id) DO UPDATE SET
                source_url            = EXCLUDED.source_url,
                source_display_name   = EXCLUDED.source_display_name,
                source_version        = EXCLUDED.source_version,
                actor                 = EXCLUDED.actor,
                created_at            = EXCLUDED.created_at,
                target_resource_count = EXCLUDED.target_resource_count,
                manifest_json         = EXCLUDED.manifest_json
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@run_id", record.RunId);
        command.Parameters.AddWithValue("@source_url", record.SourceUrl);
        command.Parameters.AddWithValue("@source_display_name", (object?)record.SourceDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@source_version", (object?)record.SourceVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("@actor", (object?)record.Actor ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at", record.CreatedAt);
        command.Parameters.AddWithValue("@target_resource_count", manifest.TargetResources.Length);
        command.Parameters.Add(new NpgsqlParameter("@manifest_json", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(manifest, _jsonOptions)
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveParityAsync(
        string runId,
        ArcGisMigrationParityArtifact parity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(parity);

        var sql = $"""
            UPDATE {_table}
            SET parity_json = @parity_json,
                parity_classification = @classification
            WHERE run_id = @run_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@run_id", runId);
        command.Parameters.AddWithValue("@classification", parity.Classification);
        command.Parameters.Add(new NpgsqlParameter("@parity_json", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(parity, _jsonOptions)
        });

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"Cannot persist parity for run '{runId}' because no manifest record exists.");
        }
    }

    public async Task<ArcGisMigrationRunListResult> ListAsync(
        ArcGisMigrationRunFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var page = Math.Max(0, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 25 : filter.PageSize, 1, 100);

        var whereClauses = new List<string>();
        var sqlParams = new List<NpgsqlParameter>();

        if (!string.IsNullOrWhiteSpace(filter.SourceUrl))
        {
            whereClauses.Add("source_url ILIKE @source_url_filter");
            sqlParams.Add(new NpgsqlParameter("@source_url_filter", $"%{filter.SourceUrl}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            // Map admin-status to the underlying parity_classification column. The 'manifest-only'
            // bucket is rows that have not yet received a parity artifact.
            if (string.Equals(filter.Status, ArcGisMigrationRunStatuses.ManifestOnly, StringComparison.OrdinalIgnoreCase))
            {
                whereClauses.Add("parity_classification IS NULL");
            }
            else
            {
                whereClauses.Add("parity_classification = @status_filter");
                sqlParams.Add(new NpgsqlParameter("@status_filter", filter.Status.ToLowerInvariant()));
            }
        }

        var whereSql = whereClauses.Count > 0
            ? " WHERE " + string.Join(" AND ", whereClauses)
            : string.Empty;

        var listSql = new StringBuilder()
            .Append("SELECT run_id, source_url, source_display_name, source_version, actor, created_at,")
            .Append(" target_resource_count, parity_classification, (parity_json IS NOT NULL) AS has_parity")
            .Append(" FROM ").Append(_table)
            .Append(whereSql)
            .Append(" ORDER BY created_at DESC, run_id ASC")
            .Append(" LIMIT @limit OFFSET @offset")
            .ToString();

        var countSql = "SELECT COUNT(*) FROM " + _table + whereSql;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        int total;
        await using (var countCommand = new NpgsqlCommand(countSql, connection))
        {
            foreach (var p in sqlParams)
            {
                countCommand.Parameters.Add(Clone(p));
            }
            total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }

        var items = new List<ArcGisMigrationRunSummary>();
        await using (var listCommand = new NpgsqlCommand(listSql, connection))
        {
            foreach (var p in sqlParams)
            {
                listCommand.Parameters.Add(Clone(p));
            }
            listCommand.Parameters.AddWithValue("@limit", pageSize);
            listCommand.Parameters.AddWithValue("@offset", page * pageSize);

            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new ArcGisMigrationRunSummary
                {
                    RunId = reader.GetString(0),
                    SourceUrl = reader.GetString(1),
                    SourceDisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    SourceVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Actor = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    TargetResourceCount = reader.GetInt32(6),
                    Status = MapStatus(reader.IsDBNull(7) ? null : reader.GetString(7)),
                    HasParity = reader.GetBoolean(8),
                });
            }
        }

        return new ArcGisMigrationRunListResult
        {
            Items = items.ToArray(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<MigrationManifestArtifact?> GetManifestAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var sql = $"SELECT manifest_json FROM {_table} WHERE run_id = @run_id";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@run_id", runId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return null;
        }

        return JsonSerializer.Deserialize<MigrationManifestArtifact>((string)result, _jsonOptions);
    }

    public async Task<ArcGisMigrationParityArtifact?> GetParityAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var sql = $"SELECT parity_json FROM {_table} WHERE run_id = @run_id";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@run_id", runId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ArcGisMigrationParityArtifact>((string)result, _jsonOptions);
    }

    private static string MapStatus(string? parityClassification)
    {
        if (string.IsNullOrWhiteSpace(parityClassification))
        {
            return ArcGisMigrationRunStatuses.ManifestOnly;
        }

        return parityClassification switch
        {
            ArcGisMigrationParityClassifications.Pass => ArcGisMigrationRunStatuses.Pass,
            ArcGisMigrationParityClassifications.Warn => ArcGisMigrationRunStatuses.Warn,
            ArcGisMigrationParityClassifications.Fail => ArcGisMigrationRunStatuses.Fail,
            _ => ArcGisMigrationRunStatuses.ManifestOnly,
        };
    }

    private static NpgsqlParameter Clone(NpgsqlParameter source)
    {
        return new NpgsqlParameter(source.ParameterName, source.Value);
    }
}
