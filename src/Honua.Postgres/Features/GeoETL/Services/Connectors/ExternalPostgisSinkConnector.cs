// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Postgres.Features.Import;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.GeoETL.Services.Connectors;

/// <summary>
/// Phase 1 external PostGIS sink. Writes the feature set to a PostGIS database identified
/// by a connector-supplied connection string that is <b>not</b> the Honua catalog — this
/// is the "load into a customer's own PostGIS" sink, distinct from
/// <c>HonuaLayerSinkConnector</c> which writes through the catalog's
/// <c>honua.create_import_table</c> insert path. Managed Npgsql + WKB, no GDAL.
/// </summary>
/// <remarks>
/// Required <see cref="ConnectorConfig.Options"/>:
/// <list type="bullet">
/// <item><c>connectionString</c> — the external PostGIS connection string.</item>
/// <item><c>table</c> — destination table name (created if missing).</item>
/// <item><c>targetSrid</c> — geometry SRID for the destination column.</item>
/// </list>
/// Optional <c>schema</c> (defaults to <c>public</c>), <c>geometryColumn</c> (defaults to
/// <c>geom</c>), and <c>batchSize</c> (defaults to 1000). Every row's <c>attributes</c>
/// JSONB carries a reserved <c>__pipeline_batch_id</c> key so a failed run can soft-delete
/// its rows (the ADR-0038 Phase 1 rollback contract). Table/schema/column identifiers are
/// validated against a strict pattern before being interpolated, since they cannot be
/// parameterized.
/// </remarks>
public sealed partial class ExternalPostgisSinkConnector : IPipelineSinkConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "external-postgis";

    /// <summary>Reserved attribute key tagging every row with its run batch id.</summary>
    public const string BatchIdPropertyKey = "__pipeline_batch_id";

    private const int DefaultBatchSize = 1000;

    /// <inheritdoc />
    public string Type => ConnectorType;

    /// <inheritdoc />
    public ConnectorRuntimeProfile RuntimeProfile => ConnectorRuntimeProfile.Managed;

    /// <inheritdoc />
    public async Task<SinkWriteResult> WriteAsync(
        ConnectorConfig config,
        IAsyncEnumerable<IFeature> features,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(features);

        var connectionString = RequireOption(config, "connectionString");
        var schema = Identifier(config.Options.TryGetValue("schema", out var rawSchema)
            && !string.IsNullOrWhiteSpace(rawSchema) ? rawSchema : "public");
        var table = Identifier(RequireOption(config, "table"));
        var geometryColumn = Identifier(config.Options.TryGetValue("geometryColumn", out var rawGeom)
            && !string.IsNullOrWhiteSpace(rawGeom) ? rawGeom : "geom");
        var targetSrid = RequireIntOption(config, "targetSrid");
        var batchSize = config.Options.TryGetValue("batchSize", out var rawBatch)
            && int.TryParse(rawBatch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : DefaultBatchSize;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureTableAsync(connection, schema, table, geometryColumn, targetSrid, cancellationToken)
            .ConfigureAwait(false);

        var wkbWriter = new WKBWriter();
        long written = 0;
        long rejected = 0;
        var buffer = new List<IFeature>(batchSize);

        await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (feature.Geometry is null)
            {
                rejected++;
                continue;
            }

            buffer.Add(feature);
            if (buffer.Count >= batchSize)
            {
                written += await InsertBatchAsync(
                        connection, schema, table, geometryColumn, targetSrid, buffer, batchId, wkbWriter, cancellationToken)
                    .ConfigureAwait(false);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            written += await InsertBatchAsync(
                    connection, schema, table, geometryColumn, targetSrid, buffer, batchId, wkbWriter, cancellationToken)
                .ConfigureAwait(false);
        }

        return new SinkWriteResult { FeaturesWritten = written, FeaturesRejected = rejected };
    }

    private static async Task EnsureTableAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        string geometryColumn,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            CREATE SCHEMA IF NOT EXISTS "{schema}";
            CREATE TABLE IF NOT EXISTS "{schema}"."{table}" (
                id          BIGSERIAL PRIMARY KEY,
                "{geometryColumn}" geometry(Geometry, {targetSrid.ToString(CultureInfo.InvariantCulture)}),
                attributes  JSONB NOT NULL
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> InsertBatchAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        string geometryColumn,
        int targetSrid,
        List<IFeature> features,
        string batchId,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        var wkbs = new byte[features.Count][];
        var attributes = new string[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            wkbs[i] = wkbWriter.Write(features[i].Geometry!);
            attributes[i] = BuildAttributesJson(features[i], batchId);
        }

        var sql = $"""
            INSERT INTO "{schema}"."{table}" ("{geometryColumn}", attributes)
            SELECT ST_SetSRID(ST_GeomFromWKB(payload.wkb), {targetSrid.ToString(CultureInfo.InvariantCulture)}),
                   payload.attributes
            FROM unnest(@wkbs, @attributes) AS payload(wkb, attributes)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("wkbs", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = wkbs;
        command.Parameters.Add("attributes", NpgsqlDbType.Array | NpgsqlDbType.Jsonb).Value = attributes;

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    private static string BuildAttributesJson(IFeature feature, string batchId)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [BatchIdPropertyKey] = batchId
        };

        if (feature.Attributes is { } table)
        {
            var names = table.GetNames();
            var values = table.GetValues();
            for (var i = 0; i < names.Length; i++)
            {
                properties[names[i]] = values[i];
            }
        }

        return JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject);
    }

    private static string Identifier(string value)
    {
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"External PostGIS sink identifier '{value}' is invalid. Identifiers must match " +
                "^[A-Za-z_][A-Za-z0-9_]*$ (they cannot be parameterized in DDL/DML).");
        }

        return value;
    }

    private static string RequireOption(ConnectorConfig config, string key)
    {
        if (!config.Options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"External PostGIS sink requires a '{key}' option.");
        }

        return value;
    }

    private static int RequireIntOption(ConnectorConfig config, string key)
    {
        if (!config.Options.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            throw new InvalidOperationException($"External PostGIS sink requires a positive integer '{key}' option.");
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();
}
