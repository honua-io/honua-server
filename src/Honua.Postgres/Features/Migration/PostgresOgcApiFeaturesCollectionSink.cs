// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Import.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
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
/// PostgreSQL/PostGIS sink for OGC API Features collection imports.
/// </summary>
/// <remarks>
/// The sink lazily provisions the target table with a stable schema:
/// <list type="bullet">
///   <item><c>source_feature_id text primary key</c> — natural key derived from the source feature id.</item>
///   <item><c>properties jsonb</c> — verbatim GeoJSON properties payload.</item>
///   <item><c>geometry geometry</c> — projected geometry parsed from the GeoJSON feature.</item>
///   <item><c>imported_at timestamptz</c> — write timestamp used for telemetry.</item>
/// </list>
/// Inserts use <c>ON CONFLICT (source_feature_id) DO UPDATE</c> so re-running the importer against
/// the same source/target combination converges to the same row set.
/// </remarks>
internal sealed partial class PostgresOgcApiFeaturesCollectionSink : IOgcApiFeaturesCollectionSink
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresOgcApiFeaturesCollectionSink> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresOgcApiFeaturesCollectionSink"/> class.
    /// </summary>
    /// <param name="dataSource">Configured Npgsql data source.</param>
    /// <param name="logger">Logger instance.</param>
    public PostgresOgcApiFeaturesCollectionSink(
        NpgsqlDataSource dataSource,
        ILogger<PostgresOgcApiFeaturesCollectionSink> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task EnsureTargetAsync(OgcApiFeaturesSinkTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        AssertSafeIdentifier(target.Schema, nameof(target.Schema));
        AssertSafeIdentifier(target.Table, nameof(target.Table));

        var schema = QuoteIdentifier(target.Schema);
        var table = QuoteIdentifier(target.Table);
        var sridLiteral = target.Srid.ToString(CultureInfo.InvariantCulture);

        var sql = $$"""
            CREATE SCHEMA IF NOT EXISTS {{schema}};
            CREATE TABLE IF NOT EXISTS {{schema}}.{{table}} (
                source_feature_id text PRIMARY KEY,
                properties jsonb NOT NULL DEFAULT '{}'::jsonb,
                geometry geometry,
                imported_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS {{schema}}.ogc_api_features_import_scopes (
                table_name text PRIMARY KEY,
                collection_id text NOT NULL,
                scope_signature text NOT NULL,
                recorded_at timestamptz NOT NULL DEFAULT now()
            );
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_indexes
                    WHERE schemaname = lower('{{target.Schema}}')
                      AND indexname = lower('{{target.Table}}') || '_geometry_gix'
                ) THEN
                    EXECUTE format(
                        'CREATE INDEX %I ON %I.%I USING GIST (geometry)',
                        lower('{{target.Table}}') || '_geometry_gix',
                        lower('{{target.Schema}}'),
                        lower('{{target.Table}}')
                    );
                END IF;
            END
            $$;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var targetLabel = string.Concat(target.Schema, ".", target.Table);
        Log.TargetEnsured(_logger, target.CollectionId, targetLabel, sridLiteral);
    }

    /// <inheritdoc />
    public async Task<int> WriteFeaturesAsync(
        OgcApiFeaturesSinkTarget target,
        IReadOnlyList<OgcApiFeaturesSinkFeature> features,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(features);
        AssertSafeIdentifier(target.Schema, nameof(target.Schema));
        AssertSafeIdentifier(target.Table, nameof(target.Table));

        if (features.Count == 0)
        {
            return 0;
        }

        var schema = QuoteIdentifier(target.Schema);
        var table = QuoteIdentifier(target.Table);
        var sridLiteral = target.Srid.ToString(CultureInfo.InvariantCulture);

        var sql = $"""
            INSERT INTO {schema}.{table} (source_feature_id, properties, geometry, imported_at)
            VALUES (
                @sourceId,
                @properties::jsonb,
                CASE WHEN @geometry IS NULL THEN NULL
                     ELSE ST_SetSRID(ST_GeomFromGeoJSON(@geometry), {sridLiteral})
                END,
                now()
            )
            ON CONFLICT (source_feature_id) DO UPDATE SET
                properties = EXCLUDED.properties,
                geometry = EXCLUDED.geometry,
                imported_at = EXCLUDED.imported_at;
            """;

        var written = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var feature in features)
            {
                if (string.IsNullOrWhiteSpace(feature.SourceFeatureId))
                {
                    continue;
                }

                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(new NpgsqlParameter("@sourceId", NpgsqlDbType.Text) { Value = feature.SourceFeatureId });
                command.Parameters.Add(new NpgsqlParameter("@properties", NpgsqlDbType.Text)
                {
                    Value = string.IsNullOrWhiteSpace(feature.PropertiesJson) ? "{}" : feature.PropertiesJson
                });
                command.Parameters.Add(new NpgsqlParameter("@geometry", NpgsqlDbType.Text)
                {
                    Value = (object?)feature.GeoJsonGeometry ?? DBNull.Value
                });

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                written++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        var targetLabel = string.Concat(target.Schema, ".", target.Table);
        Log.FeaturesWritten(_logger, target.CollectionId, targetLabel, written);
        return written;
    }

    /// <inheritdoc />
    public async Task<string?> GetLastScopeSignatureAsync(
        OgcApiFeaturesSinkTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        AssertSafeIdentifier(target.Schema, nameof(target.Schema));
        AssertSafeIdentifier(target.Table, nameof(target.Table));

        var schema = QuoteIdentifier(target.Schema);

        var sql = $"""
            SELECT scope_signature
              FROM {schema}.ogc_api_features_import_scopes
             WHERE table_name = @tableName
             LIMIT 1;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("@tableName", NpgsqlDbType.Text) { Value = target.Table });

        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is string text ? text : null;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Scope table not provisioned yet (e.g. legacy target); treat as no prior run.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task RecordScopeSignatureAsync(
        OgcApiFeaturesSinkTarget target,
        string scopeSignature,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(scopeSignature);
        AssertSafeIdentifier(target.Schema, nameof(target.Schema));
        AssertSafeIdentifier(target.Table, nameof(target.Table));

        var schema = QuoteIdentifier(target.Schema);

        var sql = $"""
            INSERT INTO {schema}.ogc_api_features_import_scopes (table_name, collection_id, scope_signature, recorded_at)
            VALUES (@tableName, @collectionId, @scopeSignature, now())
            ON CONFLICT (table_name) DO UPDATE SET
                collection_id = EXCLUDED.collection_id,
                scope_signature = EXCLUDED.scope_signature,
                recorded_at = EXCLUDED.recorded_at;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("@tableName", NpgsqlDbType.Text) { Value = target.Table });
        command.Parameters.Add(new NpgsqlParameter("@collectionId", NpgsqlDbType.Text) { Value = target.CollectionId });
        command.Parameters.Add(new NpgsqlParameter("@scopeSignature", NpgsqlDbType.Text) { Value = scopeSignature });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OgcApiFeaturesSinkColumn>> GetTargetColumnsAsync(
        OgcApiFeaturesSinkTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        AssertSafeIdentifier(target.Schema, nameof(target.Schema));
        AssertSafeIdentifier(target.Table, nameof(target.Table));

        const string sql = """
            SELECT column_name,
                   COALESCE(udt_name, data_type) AS data_type,
                   character_maximum_length,
                   is_nullable
              FROM information_schema.columns
             WHERE lower(table_schema) = lower(@schema)
               AND lower(table_name) = lower(@table)
             ORDER BY ordinal_position;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("@schema", NpgsqlDbType.Text) { Value = target.Schema });
        command.Parameters.Add(new NpgsqlParameter("@table", NpgsqlDbType.Text) { Value = target.Table });

        var columns = new List<OgcApiFeaturesSinkColumn>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var dataType = reader.IsDBNull(1) ? "unknown" : reader.GetString(1);
                int? maxLength = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                var nullable = !reader.IsDBNull(3) &&
                    string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase);

                var canonical = NormalizeDataType(dataType, maxLength);
                columns.Add(new OgcApiFeaturesSinkColumn
                {
                    Name = name,
                    DataType = canonical,
                    IsNullable = nullable
                });
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Target table not yet provisioned (race with EnsureTargetAsync); fall back to empty.
            return [];
        }

        return columns;
    }

    private static string NormalizeDataType(string udtOrType, int? maxLength)
    {
        var lower = udtOrType.ToLowerInvariant();
        return lower switch
        {
            "varchar" or "character varying" when maxLength is > 0 =>
                string.Create(CultureInfo.InvariantCulture, $"varchar({maxLength.Value})"),
            "bpchar" or "character" when maxLength is > 0 =>
                string.Create(CultureInfo.InvariantCulture, $"char({maxLength.Value})"),
            "int2" => "smallint",
            "int4" => "integer",
            "int8" => "bigint",
            "float4" => "real",
            "float8" => "double precision",
            "bool" => "boolean",
            _ => lower
        };
    }

    private static void AssertSafeIdentifier(string candidate, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException("Identifier must be non-empty.", parameterName);
        }

        foreach (var ch in candidate)
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            {
                continue;
            }

            throw new ArgumentException(
                $"Identifier '{candidate}' contains an unsafe character. Only letters, digits, and underscores are permitted.",
                parameterName);
        }

        // PostgreSQL silently truncates identifiers longer than NAMEDATALEN (63 bytes by default);
        // we do not reject them here because operator-supplied schemas may legitimately be longer
        // (e.g. test fixture schemas) and the database is the authoritative truncation point.
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 21201,
            Level = LogLevel.Debug,
            Message = "Ensured OGC API Features sink target {Target} for collection {CollectionId} with SRID {Srid}.")]
        public static partial void TargetEnsured(ILogger logger, string collectionId, string target, string srid);

        [LoggerMessage(
            EventId = 21202,
            Level = LogLevel.Information,
            Message = "Wrote {FeatureCount} OGC API Features rows to {Target} for collection {CollectionId}.")]
        public static partial void FeaturesWritten(ILogger logger, string collectionId, string target, int featureCount);
    }
}
