// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>sink.external-postgis</c> executor. Writes the input FeatureCollection to a PostGIS
/// database identified by a registered secure connection that is NOT the Honua catalog —
/// the "load into a customer's own PostGIS" sink. Managed Npgsql + WKB, no GDAL.
/// Reconciled from the GeoETL baseline ExternalPostgisSinkConnector onto the #1185
/// process/executor contract. Identifiers are validated against a strict pattern before
/// interpolation since they cannot be parameterized in DDL/DML. Every row's attributes JSONB
/// carries a reserved <c>__pipeline_batch_id</c> key for soft-delete rollback.
/// </summary>
internal sealed partial class ExternalPostgisSinkExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options,
    ISecureConnectionResolver? secureConnectionResolver = null) : IJobExecutor
{
    internal const string HandledProcessId = "sink.external-postgis";

    /// <summary>Reserved attribute key tagging every row with its run batch id.</summary>
    public const string BatchIdPropertyKey = "__pipeline_batch_id";

    private const int DefaultBatchSize = 1000;

    // Options is part of the canonical executor shape (artifact guardrails); the sink
    // result descriptor is tiny so the ceiling is not consulted, but the field keeps the
    // ctor uniform with the rest of the family.
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options = options;
    private readonly ISecureConnectionResolver? _secureConnectionResolver = secureConnectionResolver;

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);
        _ = _options;

        var resolved = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (!string.Equals(resolved, HandledProcessId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(10, "Parsing PostGIS sink inputs", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!inputs.TryGetRequired("input", out var inputUri, out var inputError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {inputError}");
        }

        if (!FeatureCollectionArtifact.TryParseDataUri(inputUri, out var source, out var parseError))
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: 'input' {parseError}");
        }

        string connectionString;
        string schema;
        string table;
        string geometryColumn;
        int targetSrid;
        int batchSize;
        try
        {
            schema = Identifier(inputs.GetOrDefault("schema", "public"));
            table = Identifier(Require(inputs, "table"));
            geometryColumn = Identifier(inputs.GetOrDefault("geometryColumn", "geom"));
            targetSrid = RequireSrid(inputs, "targetSrid");
            batchSize = inputs.TryGet("batchSize", out var rawBatch)
                && int.TryParse(rawBatch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0
                ? parsed
                : DefaultBatchSize;
        }
        catch (TransformInputException ex)
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {ex.PublicMessage}");
        }

        var resolvedConnection = await ResolveConnectionStringAsync(inputs, cancellationToken).ConfigureAwait(false);
        if (resolvedConnection.Error is not null)
        {
            return JobExecutionResult.Failed($"Invalid {HandledProcessId} inputs: {resolvedConnection.Error}");
        }

        connectionString = resolvedConnection.ConnectionString!;

        var batchId = inputs.GetOrDefault("batchId", job.OperationId);

        long written = 0;
        long rejected = 0;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await EnsureTableAsync(connection, schema, table, geometryColumn, targetSrid, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Inserting features", cancellationToken).ConfigureAwait(false);

            var wkbWriter = new WKBWriter();
            var buffer = new List<IFeature>(batchSize);

            foreach (var feature in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        }
        catch (NpgsqlException ex)
        {
            return JobExecutionResult.Failed($"{HandledProcessId} write failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(
            SinkResultArtifact.Build(
                HandledProcessId,
                ("schema", schema),
                ("table", table),
                ("featuresWritten", written),
                ("featuresRejected", rejected)),
            cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private async Task<(string? ConnectionString, string? Error)> ResolveConnectionStringAsync(
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        if (inputs.TryGet("connectionString", out _))
        {
            return (null, "connectionString is not accepted; use connectionName or connectionId.");
        }

        if (_secureConnectionResolver is null)
        {
            return (null, "secure connection resolver is not configured.");
        }

        var hasName = inputs.TryGet("connectionName", out var connectionName);
        var hasId = inputs.TryGet("connectionId", out var connectionIdText);
        if (hasName == hasId)
        {
            return (null, "exactly one of connectionName or connectionId is required.");
        }

        try
        {
            string connectionString;
            if (hasId)
            {
                if (!Guid.TryParse(connectionIdText, out var connectionId))
                {
                    return (null, "connectionId must be a valid GUID.");
                }

                connectionString = await _secureConnectionResolver.ResolveConnectionStringAsync(
                    connectionId,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                connectionString = await _secureConnectionResolver.ResolveConnectionStringAsync(
                    connectionName!,
                    cancellationToken).ConfigureAwait(false);
            }

            return string.IsNullOrWhiteSpace(connectionString)
                ? (null, "secure connection could not be resolved.")
                : (connectionString, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, "secure connection could not be resolved.");
        }
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

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAttributesJson(IFeature feature, string batchId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(BatchIdPropertyKey, batchId);

            if (feature.Attributes is { } table)
            {
                var names = table.GetNames();
                var values = table.GetValues();
                for (var i = 0; i < names.Length; i++)
                {
                    WriteAttribute(writer, names[i], values[i]);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteAttribute(Utf8JsonWriter writer, string name, object? value)
    {
        writer.WritePropertyName(name);
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case JsonElement jsonElement:
                jsonElement.WriteTo(writer);
                break;
            case string stringValue:
                writer.WriteStringValue(stringValue);
                break;
            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                break;
            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                break;
            case sbyte sbyteValue:
                writer.WriteNumberValue(sbyteValue);
                break;
            case short shortValue:
                writer.WriteNumberValue(shortValue);
                break;
            case ushort ushortValue:
                writer.WriteNumberValue(ushortValue);
                break;
            case int intValue:
                writer.WriteNumberValue(intValue);
                break;
            case uint uintValue:
                writer.WriteNumberValue(uintValue);
                break;
            case long longValue:
                writer.WriteNumberValue(longValue);
                break;
            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                break;
            case float floatValue when float.IsFinite(floatValue):
                writer.WriteNumberValue(floatValue);
                break;
            case double doubleValue when double.IsFinite(doubleValue):
                writer.WriteNumberValue(doubleValue);
                break;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string Identifier(string value)
    {
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new TransformInputException(
                $"identifier '{value}' is invalid; identifiers must match ^[A-Za-z_][A-Za-z0-9_]*$ " +
                "(they cannot be parameterized in DDL/DML).");
        }

        return value;
    }

    private static string Require(StepInputReader inputs, string key)
    {
        if (inputs.TryGet(key, out var value))
        {
            return value!;
        }

        throw new TransformInputException($"requires a '{key}' option.");
    }

    private static int RequireSrid(StepInputReader inputs, string key)
    {
        if (!inputs.TryGet(key, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new TransformInputException($"requires a positive integer '{key}' option.");
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();
}
