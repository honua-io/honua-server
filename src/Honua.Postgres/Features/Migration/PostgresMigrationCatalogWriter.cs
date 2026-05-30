// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// PostgreSQL implementation of <see cref="IMigrationCatalogWriter"/>. Persists
/// migrated workspace and layer-group records into <c>honua.services</c> using
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> so re-running an apply is idempotent.
/// </summary>
internal sealed partial class PostgresMigrationCatalogWriter : IMigrationCatalogWriter
{
    private static readonly string[] _defaultFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _defaultCapabilities = ["Query", "Extract"];

    private readonly ILogger<PostgresMigrationCatalogWriter> _logger;

    public PostgresMigrationCatalogWriter(ILogger<PostgresMigrationCatalogWriter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MigrationCatalogWriteOutcome> EnsureCatalogServiceAsync(
        string connectionString,
        MigrationCatalogServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ServiceName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent upsert: ON CONFLICT (service_name) DO NOTHING lets the apply
        // plan re-run safely against the same target without duplicating catalog rows.
        const string sql = """
            INSERT INTO honua.services (
                service_name,
                description,
                srid,
                supported_formats,
                capabilities
            )
            VALUES (@serviceName, @description, @srid, @formats, @capabilities)
            ON CONFLICT (service_name) DO NOTHING
            RETURNING service_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", request.ServiceName);
        command.Parameters.AddWithValue("@description", request.Description);
        command.Parameters.AddWithValue("@srid", request.Srid);
        command.Parameters.AddWithValue("@formats", _defaultFormats);
        command.Parameters.AddWithValue("@capabilities", _defaultCapabilities);

        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var outcome = inserted is null
            ? MigrationCatalogWriteOutcome.AlreadyExists
            : MigrationCatalogWriteOutcome.Created;

        Log.CatalogServicePersisted(_logger, request.EntryKind, request.ServiceName, outcome.ToString());
        return outcome;
    }

    public async Task<MigrationCatalogWriteOutcome> EnsureDataSourceAsync(
        string connectionString,
        MigrationDataSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DataSourceType);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent upsert on (source_kind, source_id). Slice 2 deliberately
        // does not overwrite existing rows because the source-of-truth for a
        // migrated data source is the source system being scanned, not the
        // Honua catalog.
        const string sql = """
            INSERT INTO honua.migration_data_sources (
                source_kind,
                source_id,
                data_source_type,
                workspace_name,
                display_name,
                connection_summary
            )
            VALUES (@sourceKind, @sourceId, @dataSourceType, @workspaceName, @displayName, @connectionSummary)
            ON CONFLICT (source_kind, source_id) DO NOTHING
            RETURNING source_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceKind", request.SourceKind);
        command.Parameters.AddWithValue("@sourceId", request.SourceId);
        command.Parameters.AddWithValue("@dataSourceType", request.DataSourceType);
        command.Parameters.AddWithValue("@workspaceName", (object?)request.WorkspaceName ?? DBNull.Value);
        command.Parameters.AddWithValue("@displayName", request.DisplayName ?? request.SourceId);
        command.Parameters.AddWithValue("@connectionSummary", request.ConnectionSummary);

        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var outcome = inserted is null
            ? MigrationCatalogWriteOutcome.AlreadyExists
            : MigrationCatalogWriteOutcome.Created;

        Log.DataSourcePersisted(_logger, request.DataSourceType, request.SourceId, outcome.ToString());
        return outcome;
    }

    public async Task<MigrationFeatureCopyOutcome> CopyFeatureDataAsync(
        string connectionString,
        MigrationFeatureCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetTable);

        // Defense in depth: never interpolate identifiers that did not pass the
        // safe-identifier guard. Callers should already have validated, but
        // double-check here so the writer never composes hostile SQL.
        if (!IsSafeIdentifier(request.SourceSchema) ||
            !IsSafeIdentifier(request.SourceTable) ||
            !IsSafeIdentifier(request.TargetSchema) ||
            !IsSafeIdentifier(request.TargetTable))
        {
            throw new ArgumentException("Identifiers must be ASCII letter/digit/underscore only.", nameof(request));
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Ensure the data schema exists.
        await using (var ensureSchema = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS \"{request.TargetSchema}\";",
            connection))
        {
            await ensureSchema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Confirm the source table exists in this database. If not, return
        // SourceMissing so the executor can record a manual-review step.
        const string sourceExistsSql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table
            LIMIT 1;
            """;
        await using (var sourceCheck = new NpgsqlCommand(sourceExistsSql, connection))
        {
            sourceCheck.Parameters.AddWithValue("@schema", request.SourceSchema);
            sourceCheck.Parameters.AddWithValue("@table", request.SourceTable);
            var found = await sourceCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                Log.FeatureCopySourceMissing(_logger, request.SourceSchema, request.SourceTable);
                return new MigrationFeatureCopyOutcome
                {
                    Status = MigrationFeatureCopyStatus.SourceMissing,
                    RowCount = 0
                };
            }
        }

        // Idempotency: if the target already exists, just report the current
        // row count without copying again. ON CONFLICT-style row-level guards
        // are unnecessary because we never partial-fill the target table here.
        const string targetExistsSql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table
            LIMIT 1;
            """;
        bool targetExists;
        await using (var targetCheck = new NpgsqlCommand(targetExistsSql, connection))
        {
            targetCheck.Parameters.AddWithValue("@schema", request.TargetSchema);
            targetCheck.Parameters.AddWithValue("@table", request.TargetTable);
            targetExists = (await targetCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) is not null;
        }

        var targetIdent = $"\"{request.TargetSchema}\".\"{request.TargetTable}\"";
        var sourceIdent = $"\"{request.SourceSchema}\".\"{request.SourceTable}\"";

        if (targetExists)
        {
            var existingCount = await CountRowsAsync(connection, targetIdent, cancellationToken).ConfigureAwait(false);
            Log.FeatureCopyAlreadyApplied(_logger, request.TargetSchema, request.TargetTable, existingCount);
            return new MigrationFeatureCopyOutcome
            {
                Status = MigrationFeatureCopyStatus.AlreadyApplied,
                RowCount = existingCount
            };
        }

        // First apply: create the target table with the same columns as the source
        // (including geometry typmod / SRID) then copy all rows. Wrap the
        // structure + data steps in a transaction so a failure mid-copy leaves
        // no half-populated table behind.
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var createCmd = new NpgsqlCommand(
                $"CREATE TABLE {targetIdent} (LIKE {sourceIdent} INCLUDING DEFAULTS INCLUDING CONSTRAINTS);",
                connection,
                transaction))
            {
                await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var copyCmd = new NpgsqlCommand(
                $"INSERT INTO {targetIdent} SELECT * FROM {sourceIdent};",
                connection,
                transaction))
            {
                await copyCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var rowCount = await CountRowsAsync(connection, targetIdent, cancellationToken).ConfigureAwait(false);
        Log.FeatureCopyCompleted(_logger, request.SourceSchema, request.SourceTable, request.TargetSchema, request.TargetTable, rowCount);
        return new MigrationFeatureCopyOutcome
        {
            Status = MigrationFeatureCopyStatus.Copied,
            RowCount = rowCount
        };
    }

    public async Task<MigrationCatalogWriteOutcome> EnsureStyleAsync(
        string connectionString,
        MigrationStyleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetStyleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StyleName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent upsert on (source_kind, source_id). Slice 3 deliberately
        // does not overwrite existing rows because the source-of-truth for a
        // migrated style is the source system being scanned; operators who
        // need to refresh diagnostics should delete the row and re-apply.
        const string sql = """
            INSERT INTO honua.migration_styles (
                source_kind,
                source_id,
                workspace_name,
                style_name,
                source_format,
                source_language_version,
                target_style_id,
                source_body,
                converted_body,
                converted_format,
                diagnostics,
                review_disposition
            )
            VALUES (
                @sourceKind,
                @sourceId,
                @workspaceName,
                @styleName,
                @sourceFormat,
                @sourceLanguageVersion,
                @targetStyleId,
                @sourceBody,
                @convertedBody,
                @convertedFormat,
                CAST(@diagnostics AS jsonb),
                @reviewDisposition
            )
            ON CONFLICT (source_kind, source_id) DO NOTHING
            RETURNING source_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceKind", request.SourceKind);
        command.Parameters.AddWithValue("@sourceId", request.SourceId);
        command.Parameters.AddWithValue("@workspaceName", (object?)request.WorkspaceName ?? DBNull.Value);
        command.Parameters.AddWithValue("@styleName", request.StyleName);
        command.Parameters.AddWithValue("@sourceFormat", request.SourceFormat);
        command.Parameters.AddWithValue("@sourceLanguageVersion", (object?)request.SourceLanguageVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("@targetStyleId", request.TargetStyleId);
        command.Parameters.AddWithValue("@sourceBody", (object?)request.SourceBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@convertedBody", (object?)request.ConvertedBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@convertedFormat", (object?)request.ConvertedFormat ?? DBNull.Value);
        command.Parameters.AddWithValue("@diagnostics", string.IsNullOrWhiteSpace(request.DiagnosticsJson) ? "[]" : request.DiagnosticsJson);
        command.Parameters.AddWithValue("@reviewDisposition", request.ReviewDisposition);

        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var outcome = inserted is null
            ? MigrationCatalogWriteOutcome.AlreadyExists
            : MigrationCatalogWriteOutcome.Created;

        Log.StylePersisted(_logger, request.SourceFormat, request.SourceId, request.ReviewDisposition, outcome.ToString());
        return outcome;
    }

    private static async Task<long> CountRowsAsync(NpgsqlConnection connection, string identifier, CancellationToken cancellationToken)
    {
        await using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {identifier};", connection);
        var raw = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw switch
        {
            long l => l,
            int i => i,
            _ => Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static bool IsSafeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        foreach (var ch in identifier)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(7960, LogLevel.Information, "Migration catalog writer ensured {EntryKind} service '{ServiceName}' ({Outcome})")]
        public static partial void CatalogServicePersisted(ILogger logger, string entryKind, string serviceName, string outcome);

        [LoggerMessage(7961, LogLevel.Information, "Migration catalog writer ensured {DataSourceType} data source '{SourceId}' ({Outcome})")]
        public static partial void DataSourcePersisted(ILogger logger, string dataSourceType, string sourceId, string outcome);

        [LoggerMessage(7962, LogLevel.Information, "Migration feature copy completed: {SourceSchema}.{SourceTable} -> {TargetSchema}.{TargetTable} ({RowCount} rows)")]
        public static partial void FeatureCopyCompleted(ILogger logger, string sourceSchema, string sourceTable, string targetSchema, string targetTable, long rowCount);

        [LoggerMessage(7963, LogLevel.Information, "Migration feature copy already applied: {TargetSchema}.{TargetTable} present ({RowCount} rows)")]
        public static partial void FeatureCopyAlreadyApplied(ILogger logger, string targetSchema, string targetTable, long rowCount);

        [LoggerMessage(7964, LogLevel.Warning, "Migration feature copy source missing: {SourceSchema}.{SourceTable}")]
        public static partial void FeatureCopySourceMissing(ILogger logger, string sourceSchema, string sourceTable);

        [LoggerMessage(7965, LogLevel.Information, "Migration catalog writer ensured {SourceFormat} style '{SourceId}' (disposition={Disposition}, {Outcome})")]
        public static partial void StylePersisted(ILogger logger, string sourceFormat, string sourceId, string disposition, string outcome);
    }
}
