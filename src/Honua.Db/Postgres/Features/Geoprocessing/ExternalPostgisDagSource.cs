// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Npgsql;

namespace Honua.Postgres.Features.Geoprocessing;

/// <summary>
/// <c>source.postgis</c> DAG connector. Streams features from a customer-owned
/// PostGIS table/view identified by a registered secure connection — the read-side
/// mirror of <c>sink.external-postgis</c>. The connection string is resolved by the
/// executor from the same secure-connection secret handling the sink uses (raw
/// connection strings never cross the DAG); this reader only consumes the resolved
/// string. Geometry is projected to GeoJSON server-side with <c>ST_AsGeoJSON</c> so
/// no native client geometry library is needed, and rows stream through a forward-only
/// reader to keep memory bounded for large extracts. Identifiers are validated against
/// a strict pattern before interpolation since they cannot be parameterised in SQL.
/// </summary>
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

        var schema = Identifier(string.IsNullOrWhiteSpace(request.PostgisSchema) ? "public" : request.PostgisSchema!);
        var table = Identifier(Require(request.PostgisTable, "table"));
        var geometryColumn = Identifier(string.IsNullOrWhiteSpace(request.PostgisGeometryColumn) ? "geom" : request.PostgisGeometryColumn!);
        var sql = BuildSql(schema, table, geometryColumn, request, out var parameters);

        await using var connection = await OpenConnectionAsync(request, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

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

    private static string BuildSql(
        string schema,
        string table,
        string geometryColumn,
        DagSourceRequest request,
        out List<(string Name, object Value)> parameters)
    {
        parameters = [];
        var sql = new StringBuilder();

        // ST_AsGeoJSON projects the geometry server-side so the reader needs no native
        // geometry library; the alias is filtered out of the attribute projection.
        sql.Append("SELECT *, ST_AsGeoJSON(\"")
            .Append(geometryColumn)
            .Append("\") AS __honua_geom_geojson FROM \"")
            .Append(schema)
            .Append("\".\"")
            .Append(table)
            .Append('"');

        var predicates = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(request.Where))
        {
            // Operator-supplied predicate (validated upstream); appended verbatim.
            predicates.Add($"({request.Where})");
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
                $"\"{geometryColumn}\" && ST_MakeEnvelope("
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

        return sql.ToString();
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();
}
