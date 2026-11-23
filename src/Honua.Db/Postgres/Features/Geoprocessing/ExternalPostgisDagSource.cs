// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.GeoServicesSql;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Queries.Filters;
using Npgsql;

namespace Honua.Db.Postgres.Features.Geoprocessing;

internal sealed partial class ExternalPostgisDagSource : IDagFeatureSource
{
    private readonly NpgsqlDataSource? _injectedDataSource;

    public ExternalPostgisDagSource()
    {
    }

    internal ExternalPostgisDagSource(NpgsqlDataSource dataSource)
    {
        _injectedDataSource = dataSource;
    }

    public string SourceId => "source.postgis";

    public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
        DagSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceTable = GetSourceTable(request);
        var filter = ParseFilter(request);

        await using var connection = await OpenConnectionAsync(request, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // A SELECT predicate can invoke a writing function (for example nextval).
        // Token validation limits statement shape; PostgreSQL must enforce read-only execution.
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        SqlFragment? predicate = null;
        if (filter is not null)
        {
            var context = await ReadFilterContextAsync(transaction, request, cancellationToken).ConfigureAwait(false);
            predicate = new PostgresSqlFilterTranslator(geometryColumn: sourceTable.GeometryColumn).Translate(filter, context);
        }
        var (sql, parameters) = BuildSql(sourceTable, request, predicate);
        await using var command = PostgresSqlSafety.CreateReadCommand(connection, sql);
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            // Let PostgreSQL infer the watermark type from its column (timestamp,
            // number or text). Sending it as a typed text parameter makes a normal
            // timestamptz >= @since comparison fail with operator-not-found.
            command.Parameters.AddWithValue(name, NpgsqlTypes.NpgsqlDbType.Unknown, value);
        }

        BindFilterParameters(command, predicate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var geometryOrdinal = reader.GetOrdinal("__honua_geom_geojson");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? geometryGeoJson = await reader.IsDBNullAsync(geometryOrdinal, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(geometryOrdinal);

            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i == geometryOrdinal)
                {
                    continue;
                }

                var columnName = reader.GetName(i);
                if (string.Equals(columnName, sourceTable.GeometryColumn, StringComparison.Ordinal))
                {
                    // Geometry is already projected through ST_AsGeoJSON. Do not ask
                    // the scalar Npgsql reader to materialize its unsupported native type.
                    continue;
                }

                attributes[columnName] = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(i);
            }

            yield return new DagSourceFeature
            {
                GeometryGeoJson = geometryGeoJson,
                Attributes = attributes
            };
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(DagSourceRequest request, CancellationToken cancellationToken)
    {
        if (_injectedDataSource is not null)
        {
            return await _injectedDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(request.PostgisConnectionString))
        {
            throw new InvalidOperationException("source.postgis requires a resolved secure connection.");
        }

        var connection = new NpgsqlConnection(request.PostgisConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static FilterExpression? ParseFilter(DagSourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Where))
        {
            return null;
        }
        PostgresSqlSafety.ValidateReadOnlySingleStatement("SELECT 1 WHERE (" + request.Where + ")");
        return new GeoServicesSqlParser().Parse(request.Where, static text => text);
    }

    private static void BindFilterParameters(NpgsqlCommand command, SqlFragment? predicate)
    {
        if (predicate is null)
        {
            return;
        }
        for (var i = 0; i < predicate.Parameters.Count; i++)
        {
            var value = predicate.Parameters[i] ?? DBNull.Value;
            if (value is string text)
            {
                command.Parameters.AddWithValue($"p{i}", NpgsqlTypes.NpgsqlDbType.Unknown, text);
            }
            else
            {
                command.Parameters.AddWithValue($"p{i}", value);
            }
        }
    }

    private sealed record SourceTable(string Schema, string Table, string GeometryColumn);

    private static SourceTable GetSourceTable(DagSourceRequest request) => new(
        Identifier(string.IsNullOrWhiteSpace(request.PostgisSchema) ? "public" : request.PostgisSchema!),
        Identifier(Require(request.PostgisTable, "table")),
        Identifier(string.IsNullOrWhiteSpace(request.PostgisGeometryColumn) ? "geom" : request.PostgisGeometryColumn!));

    private static async Task<FilterTranslationContext> ReadFilterContextAsync(
        NpgsqlTransaction transaction,
        DagSourceRequest request,
        CancellationToken cancellationToken)
    {
        var sourceTable = GetSourceTable(request);
        var srid = request.OutputSrid ?? 4326;
        // pg_attribute covers tables, views, materialized views, foreign and partitioned tables.
        // Names that differ only by case are left out so a filter cannot bind the wrong one.
        await using var command = new NpgsqlCommand("""
            SELECT a.attname FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
              AND c.relkind IN ('r', 'v', 'm', 'f', 'p')
              AND a.attnum > 0 AND NOT a.attisdropped
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_attribute other
                  WHERE other.attrelid = a.attrelid AND other.attnum > 0 AND NOT other.attisdropped
                    AND other.attnum <> a.attnum AND lower(other.attname) = lower(a.attname))
            ORDER BY a.attnum
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("schema", sourceTable.Schema);
        command.Parameters.AddWithValue("table", sourceTable.Table);
        var fields = new List<FilterTranslationContext.ContextField>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            // Physical columns retain their database types; no JSON attribute casts are needed.
            fields.Add(new(name, MetadataV2FieldType.Unknown,
                string.Equals(name, sourceTable.GeometryColumn, StringComparison.Ordinal), false));
        }
        return FilterTranslationContext.FromColumns(fields, null, sourceTable.GeometryColumn, srid,
            GeometryType.None, sourceTable.Table, isGeographic: srid == 4326);
    }

    private static (string Sql, List<(string Name, object Value)> Parameters) BuildSql(
        SourceTable sourceTable,
        DagSourceRequest request,
        SqlFragment? predicate)
    {
        List<(string Name, object Value)> parameters = [];
        var sql = new StringBuilder();

        // ST_AsGeoJSON projects the geometry server-side so the reader needs no native
        // geometry library; the alias is filtered out of the attribute projection.
        sql.Append("SELECT *, ST_AsGeoJSON(\"")
            .Append(sourceTable.GeometryColumn)
            .Append("\") AS __honua_geom_geojson FROM \"")
            .Append(sourceTable.Schema)
            .Append("\".\"")
            .Append(sourceTable.Table)
            .Append('"');

        var predicates = new List<string>(3);

        if (predicate is not null)
        {
            predicates.Add($"({predicate.Sql})");
        }

        if (!string.IsNullOrWhiteSpace(request.Since) && !string.IsNullOrWhiteSpace(request.WatermarkField))
        {
            var watermarkColumn = Identifier(request.WatermarkField!);
            predicates.Add($"\"{watermarkColumn}\" >= @__since");
            parameters.Add(("__since", request.Since!));
        }

        if (!string.IsNullOrWhiteSpace(request.Bbox))
        {
            var (minX, minY, maxX, maxY) = ParseBbox(request.Bbox);
            var srid = request.OutputSrid ?? 4326;
            predicates.Add(
                $"\"{sourceTable.GeometryColumn}\" && ST_MakeEnvelope("
                + $"{minX.ToString(CultureInfo.InvariantCulture)},"
                + $"{minY.ToString(CultureInfo.InvariantCulture)},"
                + $"{maxX.ToString(CultureInfo.InvariantCulture)},"
                + $"{maxY.ToString(CultureInfo.InvariantCulture)},"
                + $"{srid.ToString(CultureInfo.InvariantCulture)})");
        }

        if (predicates.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", predicates));
        }

        return (sql.ToString(), parameters);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) ParseBbox(string bbox)
    {
        var parts = bbox.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY))
        {
            throw new InvalidOperationException("source.postgis bbox must be 'minX,minY,maxX,maxY'.");
        }

        return (minX, minY, maxX, maxY);
    }

    private static string Identifier(string value)
    {
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"identifier '{value}' is invalid; identifiers must match ^[A-Za-z_][A-Za-z0-9_]*$.");
        }

        return value;
    }

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"source.postgis requires a '{name}' option.");
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z")]
    private static partial Regex IdentifierRegex();
}
