// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Honua.Core.Features.AnomalyDetection.Abstractions;
using Honua.Core.Features.AnomalyDetection.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.AnomalyDetection;

/// <summary>
/// Deterministic anomaly analyzer backed by PostGIS aggregate queries.
/// Detects geometry and attribute anomalies using SQL-based statistical analysis.
/// </summary>
internal sealed partial class PostgresAnomalyAnalyzer(
    IDatabaseConnectionProvider connectionProvider,
    ILogger<PostgresAnomalyAnalyzer> logger) : IAnomalyAnalyzer
{
    // ActivitySource for tracing (same name as HonuaTelemetry for correlation)
    private static readonly ActivitySource AnomalyActivitySource = new("Honua", "1.0.0");

    private const int NullClusterThresholdPercent = 50;
    private const double HighCardinalityRatio = 0.95;
    private const double OutlierStdDevMultiplier = 3.0;
    private const double SuspiciousAreaPerimeterRatio = 0.001;

    /// <inheritdoc />
    public async Task<AnomalyReport> AnalyzeAsync(
        AnomalyAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = AnomalyActivitySource.StartActivity("Anomaly.Analyze");
        activity?.SetTag("anomaly.table", request.TableName);

        await using var connection = await connectionProvider.OpenConnectionAsync(cancellationToken);

        var totalCount = await GetFeatureCountAsync(connection, request, cancellationToken);

        var geometryAnomalies = request.GeometryColumn is not null
            ? await DetectGeometryAnomaliesAsync(connection, request, cancellationToken)
            : [];

        var attributeAnomalies = await DetectAttributeAnomaliesAsync(
            connection, request, totalCount, cancellationToken);

        var report = new AnomalyReport
        {
            LayerName = request.LayerName,
            FeaturesScanned = totalCount,
            GeometryAnomalies = geometryAnomalies,
            AttributeAnomalies = attributeAnomalies,
        };

        activity?.SetTag("anomaly.geometry_count", geometryAnomalies.Count);
        activity?.SetTag("anomaly.attribute_count", attributeAnomalies.Count);

        AnomalyLog.AnalysisCompleted(
            logger, request.LayerName, geometryAnomalies.Count, attributeAnomalies.Count, totalCount);

        return report;
    }

    private static async Task<long> GetFeatureCountAsync(
        DbConnection connection, AnomalyAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var table = QuoteIdentifier(request.TableName);
        var oidCol = QuoteIdentifier(request.ObjectIdColumn);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = request.ScanLimit > 0
            ? $"SELECT COUNT(*) FROM (SELECT 1 FROM {table} ORDER BY {oidCol} LIMIT {request.ScanLimit}) t"
            : $"SELECT COUNT(*) FROM {table}";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    private static async Task<List<GeometryAnomaly>> DetectGeometryAnomaliesAsync(
        DbConnection connection, AnomalyAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var anomalies = new List<GeometryAnomaly>();
        var table = QuoteIdentifier(request.TableName);
        var geomCol = QuoteIdentifier(request.GeometryColumn!);
        var oidCol = QuoteIdentifier(request.ObjectIdColumn);
        var scanLimit = request.ScanLimit;

        // 1. Invalid geometries
        await DetectInvalidGeometriesAsync(
            connection, table, geomCol, oidCol, scanLimit,
            request.MaxSampleFeatures, anomalies, cancellationToken);

        // 2. Empty/null geometries
        await DetectEmptyGeometriesAsync(
            connection, table, geomCol, oidCol, scanLimit,
            request.MaxSampleFeatures, anomalies, cancellationToken);

        // 3. SRID mismatches
        await DetectSridMismatchesAsync(
            connection, table, geomCol, oidCol, scanLimit,
            request.DeclaredSrid, request.MaxSampleFeatures, anomalies, cancellationToken);

        // 4. Suspicious area/perimeter ratios (polygons only)
        await DetectSuspiciousAreaPerimeterAsync(
            connection, table, geomCol, oidCol, scanLimit,
            request.DeclaredSrid, request.MaxSampleFeatures, anomalies, cancellationToken);

        // 5. Duplicate vertices
        await DetectDuplicateVerticesAsync(
            connection, table, geomCol, oidCol, scanLimit,
            request.MaxSampleFeatures, anomalies, cancellationToken);

        return anomalies;
    }

    private static string BuildScanCte(string table, string columns, int scanLimit, string oidCol)
    {
        // Relies on connection search_path (set by PostgresDatabaseConnectionProvider)
        // rather than hardcoding a schema prefix.
        // ORDER BY oidCol ensures a deterministic row set when LIMIT is active.
        return scanLimit > 0
            ? $"WITH scanned AS (SELECT {columns} FROM {table} ORDER BY {oidCol} LIMIT {scanLimit})"
            : $"WITH scanned AS (SELECT {columns} FROM {table})";
    }

    private static async Task DetectInvalidGeometriesAsync(
        DbConnection connection, string table, string geomCol, string oidCol,
        int scanLimit, int maxSamples, List<GeometryAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        var cte = BuildScanCte(table, $"{oidCol}, {geomCol}", scanLimit, oidCol);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {cte}
            SELECT COUNT(*) FROM scanned
            WHERE {geomCol} IS NOT NULL AND NOT ST_IsValid({geomCol})
            """;

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                {cte}
                SELECT {oidCol} FROM scanned
                WHERE {geomCol} IS NOT NULL AND NOT ST_IsValid({geomCol})
                ORDER BY {oidCol}
                LIMIT {maxSamples}
                """;
            var sampleIds = await ReadSampleIdsDirectAsync(sampleCmd, cancellationToken);

            anomalies.Add(new GeometryAnomaly
            {
                Type = GeometryAnomalyType.InvalidGeometry,
                Reason = $"{count} feature(s) have topologically invalid geometry (self-intersection, unclosed rings, etc.)",
                Severity = AnomalySeverity.Error,
                AffectedCount = count,
                SampleFeatureIds = sampleIds,
            });
        }
    }

    private static async Task DetectEmptyGeometriesAsync(
        DbConnection connection, string table, string geomCol, string oidCol,
        int scanLimit, int maxSamples, List<GeometryAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        var cte = BuildScanCte(table, $"{oidCol}, {geomCol}", scanLimit, oidCol);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {cte}
            SELECT COUNT(*) FROM scanned
            WHERE {geomCol} IS NULL OR ST_IsEmpty({geomCol})
            """;

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                {cte}
                SELECT {oidCol} FROM scanned
                WHERE {geomCol} IS NULL OR ST_IsEmpty({geomCol})
                ORDER BY {oidCol}
                LIMIT {maxSamples}
                """;
            var sampleIds = await ReadSampleIdsDirectAsync(sampleCmd, cancellationToken);

            anomalies.Add(new GeometryAnomaly
            {
                Type = GeometryAnomalyType.EmptyGeometry,
                Reason = $"{count} feature(s) have null or empty geometry",
                Severity = AnomalySeverity.Warning,
                AffectedCount = count,
                SampleFeatureIds = sampleIds,
            });
        }
    }

    private static async Task DetectSridMismatchesAsync(
        DbConnection connection, string table, string geomCol, string oidCol,
        int scanLimit, int declaredSrid, int maxSamples,
        List<GeometryAnomaly> anomalies, CancellationToken cancellationToken)
    {
        var cte = BuildScanCte(table, $"{oidCol}, {geomCol}", scanLimit, oidCol);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {cte}
            SELECT COUNT(*) FROM scanned
            WHERE {geomCol} IS NOT NULL AND ST_SRID({geomCol}) != @srid
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@srid", declaredSrid));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                {cte}
                SELECT {oidCol} FROM scanned
                WHERE {geomCol} IS NOT NULL AND ST_SRID({geomCol}) != @srid
                ORDER BY {oidCol}
                LIMIT {maxSamples}
                """;
            sampleCmd.Parameters.Add(new NpgsqlParameter("@srid", declaredSrid));
            var sampleIds = await ReadSampleIdsDirectAsync(sampleCmd, cancellationToken);

            anomalies.Add(new GeometryAnomaly
            {
                Type = GeometryAnomalyType.SridMismatch,
                Reason = $"{count} feature(s) have SRID different from declared SRID {declaredSrid}",
                Severity = AnomalySeverity.Error,
                AffectedCount = count,
                SampleFeatureIds = sampleIds,
            });
        }
    }

    private static async Task DetectSuspiciousAreaPerimeterAsync(
        DbConnection connection, string table, string geomCol, string oidCol,
        int scanLimit, int declaredSrid, int maxSamples, List<GeometryAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        var cte = BuildScanCte(table, $"{oidCol}, {geomCol}", scanLimit, oidCol);

        // Geography cast requires WGS84 — transform projected CRS first,
        // matching the pattern in GeometryProcessor.GetGeographyOperand.
        var geogExpr = declaredSrid != 4326
            ? $"ST_Transform({geomCol}, 4326)::geography"
            : $"{geomCol}::geography";

        // Exclude rows whose stored SRID differs from the declared SRID;
        // ST_Transform uses the stored SRID as source, so mismatched or zero SRIDs
        // would produce incorrect results or abort the query entirely.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {cte}
            SELECT COUNT(*) FROM scanned
            WHERE {geomCol} IS NOT NULL
              AND ST_SRID({geomCol}) = @srid
              AND ST_GeometryType({geomCol}) IN ('ST_Polygon', 'ST_MultiPolygon')
              AND ST_Perimeter({geogExpr}) > 0
              AND ST_Area({geogExpr}) / ST_Perimeter({geogExpr}) < @ratio
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@srid", declaredSrid));
        cmd.Parameters.Add(new NpgsqlParameter("@ratio", SuspiciousAreaPerimeterRatio));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                {cte}
                SELECT {oidCol} FROM scanned
                WHERE {geomCol} IS NOT NULL
                  AND ST_SRID({geomCol}) = @srid
                  AND ST_GeometryType({geomCol}) IN ('ST_Polygon', 'ST_MultiPolygon')
                  AND ST_Perimeter({geogExpr}) > 0
                  AND ST_Area({geogExpr}) / ST_Perimeter({geogExpr}) < @ratio
                ORDER BY {oidCol}
                LIMIT {maxSamples}
                """;
            sampleCmd.Parameters.Add(new NpgsqlParameter("@srid", declaredSrid));
            sampleCmd.Parameters.Add(new NpgsqlParameter("@ratio", SuspiciousAreaPerimeterRatio));
            var sampleIds = await ReadSampleIdsDirectAsync(sampleCmd, cancellationToken);

            anomalies.Add(new GeometryAnomaly
            {
                Type = GeometryAnomalyType.SuspiciousAreaPerimeterRatio,
                Reason = $"{count} polygon(s) have suspiciously small area relative to perimeter (sliver polygons)",
                Severity = AnomalySeverity.Warning,
                AffectedCount = count,
                SampleFeatureIds = sampleIds,
            });
        }
    }

    private static async Task DetectDuplicateVerticesAsync(
        DbConnection connection, string table, string geomCol, string oidCol,
        int scanLimit, int maxSamples, List<GeometryAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        var cte = BuildScanCte(table, $"{oidCol}, {geomCol}", scanLimit, oidCol);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {cte}
            SELECT COUNT(*) FROM scanned
            WHERE {geomCol} IS NOT NULL
              AND ST_NPoints({geomCol}) != ST_NPoints(ST_RemoveRepeatedPoints({geomCol}))
            """;

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                {cte}
                SELECT {oidCol} FROM scanned
                WHERE {geomCol} IS NOT NULL
                  AND ST_NPoints({geomCol}) != ST_NPoints(ST_RemoveRepeatedPoints({geomCol}))
                ORDER BY {oidCol}
                LIMIT {maxSamples}
                """;
            var sampleIds = await ReadSampleIdsDirectAsync(sampleCmd, cancellationToken);

            anomalies.Add(new GeometryAnomaly
            {
                Type = GeometryAnomalyType.DuplicateVertices,
                Reason = $"{count} feature(s) have duplicate consecutive vertices",
                Severity = AnomalySeverity.Info,
                AffectedCount = count,
                SampleFeatureIds = sampleIds,
            });
        }
    }

    private static async Task<List<AttributeAnomaly>> DetectAttributeAnomaliesAsync(
        DbConnection connection, AnomalyAnalysisRequest request,
        long totalCount, CancellationToken cancellationToken)
    {
        var anomalies = new List<AttributeAnomaly>();
        if (totalCount == 0) return anomalies;

        var table = QuoteIdentifier(request.TableName);
        var oidCol = QuoteIdentifier(request.ObjectIdColumn);
        var scanLimit = request.ScanLimit;

        foreach (var field in request.AttributeColumns)
        {
            var col = QuoteIdentifier(field.Name);

            // 1. Null cluster detection
            await DetectNullClustersAsync(
                connection, table, col, oidCol, scanLimit,
                totalCount, field, request.MaxSampleFeatures, anomalies, cancellationToken);

            // 2. High cardinality for text fields
            if (field.DataType == AnomalyFieldDataType.Text)
            {
                await DetectHighCardinalityAsync(
                    connection, table, col, oidCol, scanLimit,
                    field, anomalies, cancellationToken);
            }

            // 3. Numeric outliers
            if (field.DataType == AnomalyFieldDataType.Numeric)
            {
                await DetectNumericOutliersAsync(
                    connection, table, col, oidCol, scanLimit,
                    field, request.MaxSampleFeatures, anomalies, cancellationToken);
            }
        }

        return anomalies;
    }

    private static async Task DetectNullClustersAsync(
        DbConnection connection, string table, string col, string oidCol,
        int scanLimit, long totalCount, AnomalyFieldDescriptor field,
        int maxSamples, List<AttributeAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        var cte = BuildScanCte(table, $"{oidCol}, {col}", scanLimit, oidCol);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {cte}
            SELECT COUNT(*) FROM scanned WHERE {col} IS NULL
            """;

        var nullCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        var effectiveTotal = totalCount > 0 ? totalCount : 1;
        var nullPercent = (int)(nullCount * 100 / effectiveTotal);

        if (nullPercent >= NullClusterThresholdPercent)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                {cte}
                SELECT {oidCol} FROM scanned
                WHERE {col} IS NULL
                ORDER BY {oidCol}
                LIMIT {maxSamples}
                """;
            var sampleIds = await ReadSampleIdsDirectAsync(sampleCmd, cancellationToken);

            anomalies.Add(new AttributeAnomaly
            {
                Type = AttributeAnomalyType.NullCluster,
                FieldName = field.Name,
                Reason = $"{nullPercent}% of values are null ({nullCount}/{effectiveTotal})",
                Severity = AnomalySeverity.Warning,
                AffectedCount = (int)Math.Min(nullCount, int.MaxValue),
                SampleFeatureIds = sampleIds,
            });
        }
    }

    private static async Task DetectHighCardinalityAsync(
        DbConnection connection, string table, string col, string oidCol,
        int scanLimit, AnomalyFieldDescriptor field,
        List<AttributeAnomaly> anomalies, CancellationToken cancellationToken)
    {
        var cte = BuildScanCte(table, col, scanLimit, oidCol);

        // Compute both distinct and non-null counts in a single scan.
        // COUNT({col}) excludes nulls, giving the correct denominator.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {cte}
            SELECT COUNT(DISTINCT {col}), COUNT({col})
            FROM scanned
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;

        var distinctCount = reader.GetInt64(0);
        var nonNullCount = reader.GetInt64(1);
        await reader.CloseAsync();

        if (nonNullCount > 10 && distinctCount > 0)
        {
            var ratio = (double)distinctCount / nonNullCount;
            if (ratio >= HighCardinalityRatio)
            {
                anomalies.Add(new AttributeAnomaly
                {
                    Type = AttributeAnomalyType.HighCardinality,
                    FieldName = field.Name,
                    Reason = $"High cardinality: {distinctCount} distinct values out of {nonNullCount} non-null records ({ratio:P0}). May be a unique identifier rather than a categorical field.",
                    Severity = AnomalySeverity.Info,
                    AffectedCount = (int)Math.Min(distinctCount, int.MaxValue),
                });
            }
        }
    }

    private static async Task DetectNumericOutliersAsync(
        DbConnection connection, string table, string col, string oidCol,
        int scanLimit, AnomalyFieldDescriptor field,
        int maxSamples, List<AttributeAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        var cte = BuildScanCte(table, $"{oidCol}, {col}", scanLimit, oidCol);

        // First compute mean and stddev within the scan window
        await using var statsCmd = connection.CreateCommand();
        statsCmd.CommandText = $"""
            {cte}
            SELECT AVG({col}::double precision) AS mean,
                   STDDEV_POP({col}::double precision) AS stddev,
                   COUNT(*) AS cnt
            FROM scanned
            WHERE {col} IS NOT NULL
            """;

        await using var statsReader = await statsCmd.ExecuteReaderAsync(cancellationToken);
        if (!await statsReader.ReadAsync(cancellationToken)) return;
        if (statsReader.IsDBNull(0) || statsReader.IsDBNull(1)) return;

        var mean = statsReader.GetDouble(0);
        var stddev = statsReader.GetDouble(1);
        var count = statsReader.GetInt64(2);
        await statsReader.CloseAsync();

        if (stddev <= 0 || count < 10) return;

        var lowerBound = mean - OutlierStdDevMultiplier * stddev;
        var upperBound = mean + OutlierStdDevMultiplier * stddev;

        // Count outliers within the scan window
        await using var outlierCmd = connection.CreateCommand();
        outlierCmd.CommandText = $"""
            {cte}
            SELECT COUNT(*) FROM scanned
            WHERE {col} IS NOT NULL
              AND ({col}::double precision < @lower OR {col}::double precision > @upper)
            """;
        outlierCmd.Parameters.Add(new NpgsqlParameter("@lower", lowerBound));
        outlierCmd.Parameters.Add(new NpgsqlParameter("@upper", upperBound));

        var outlierCount = Convert.ToInt32(await outlierCmd.ExecuteScalarAsync(cancellationToken));
        if (outlierCount > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                {cte}
                SELECT {oidCol} FROM scanned
                WHERE {col} IS NOT NULL
                  AND ({col}::double precision < @lower OR {col}::double precision > @upper)
                ORDER BY {oidCol}
                LIMIT {maxSamples}
                """;
            sampleCmd.Parameters.Add(new NpgsqlParameter("@lower", lowerBound));
            sampleCmd.Parameters.Add(new NpgsqlParameter("@upper", upperBound));
            var sampleIds = await ReadSampleIdsDirectAsync(sampleCmd, cancellationToken);

            anomalies.Add(new AttributeAnomaly
            {
                Type = AttributeAnomalyType.NumericOutlier,
                FieldName = field.Name,
                Reason = $"{outlierCount} value(s) beyond {OutlierStdDevMultiplier} standard deviations from mean (mean={mean:F2}, stddev={stddev:F2})",
                Severity = AnomalySeverity.Warning,
                AffectedCount = outlierCount,
                SampleFeatureIds = sampleIds,
            });
        }
    }

    private static async Task<List<long>> ReadSampleIdsDirectAsync(
        DbCommand cmd, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>
    /// Validates and quotes a SQL identifier for safe interpolation.
    /// Matches the project-wide QuoteIdentifier pattern.
    /// </summary>
    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !ValidIdentifierRegex().IsMatch(identifier))
        {
            throw new ArgumentException($"Invalid SQL identifier: '{identifier}'.");
        }

        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidIdentifierRegex();
}
