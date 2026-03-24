// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Diagnostics;
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
internal sealed class PostgresAnomalyAnalyzer(
    IDatabaseConnectionProvider connectionProvider,
    ILogger<PostgresAnomalyAnalyzer> logger) : IAnomalyAnalyzer
{
    private static readonly ActivitySource AnomalyActivitySource = new("Honua.AnomalyDetection", "1.0.0");

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
            ? await DetectGeometryAnomaliesAsync(connection, request, totalCount, cancellationToken)
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

        logger.LogInformation(
            "Anomaly analysis for {Layer}: {GeomCount} geometry, {AttrCount} attribute anomalies in {Count} features",
            request.LayerName, geometryAnomalies.Count, attributeAnomalies.Count, totalCount);

        return report;
    }

    private static async Task<long> GetFeatureCountAsync(
        DbConnection connection, AnomalyAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = request.ScanLimit > 0
            ? $"SELECT LEAST(COUNT(*), {request.ScanLimit}) FROM honua.{SanitizeIdentifier(request.TableName)}"
            : $"SELECT COUNT(*) FROM honua.{SanitizeIdentifier(request.TableName)}";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    private static async Task<List<GeometryAnomaly>> DetectGeometryAnomaliesAsync(
        DbConnection connection, AnomalyAnalysisRequest request,
        long totalCount, CancellationToken cancellationToken)
    {
        var anomalies = new List<GeometryAnomaly>();
        var table = SanitizeIdentifier(request.TableName);
        var geomCol = SanitizeIdentifier(request.GeometryColumn!);
        var oidCol = SanitizeIdentifier(request.ObjectIdColumn);
        var scanLimitClause = request.ScanLimit > 0 ? $" LIMIT {request.ScanLimit}" : "";

        // 1. Invalid geometries
        await DetectInvalidGeometriesAsync(
            connection, table, geomCol, oidCol, scanLimitClause,
            request.MaxSampleFeatures, anomalies, cancellationToken);

        // 2. Empty/null geometries
        await DetectEmptyGeometriesAsync(
            connection, table, geomCol, oidCol, scanLimitClause,
            request.MaxSampleFeatures, anomalies, cancellationToken);

        // 3. SRID mismatches
        await DetectSridMismatchesAsync(
            connection, table, geomCol, oidCol, scanLimitClause,
            request.DeclaredSrid, request.MaxSampleFeatures, anomalies, cancellationToken);

        // 4. Suspicious area/perimeter ratios (polygons only)
        await DetectSuspiciousAreaPerimeterAsync(
            connection, table, geomCol, oidCol, scanLimitClause,
            request.MaxSampleFeatures, anomalies, cancellationToken);

        // 5. Duplicate vertices
        await DetectDuplicateVerticesAsync(
            connection, table, geomCol, oidCol, scanLimitClause,
            request.MaxSampleFeatures, anomalies, cancellationToken);

        return anomalies;
    }

    private static async Task DetectInvalidGeometriesAsync(
        DbConnection connection, string table, string geomCol, string oidCol,
        string scanLimitClause, int maxSamples, List<GeometryAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            WITH scanned AS (
                SELECT {oidCol}, {geomCol}
                FROM honua.{table}
                WHERE {geomCol} IS NOT NULL
                {scanLimitClause}
            )
            SELECT COUNT(*) AS cnt,
                   ARRAY_AGG({oidCol} ORDER BY {oidCol}) FILTER (WHERE NOT ST_IsValid({geomCol})) AS sample_ids
            FROM (
                SELECT {oidCol}, {geomCol}, ROW_NUMBER() OVER (ORDER BY {oidCol}) AS rn
                FROM scanned
                WHERE NOT ST_IsValid({geomCol})
            ) invalid
            WHERE rn <= {maxSamples + 1}
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            if (count > 0)
            {
                var sampleIds = ReadSampleIds(reader, 1, maxSamples);
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
    }

    private static async Task DetectEmptyGeometriesAsync(
        DbConnection connection, string table, string geomCol, string oidCol,
        string scanLimitClause, int maxSamples, List<GeometryAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) AS cnt
            FROM (
                SELECT {oidCol}
                FROM honua.{table}
                WHERE {geomCol} IS NULL OR ST_IsEmpty({geomCol})
                {scanLimitClause}
            ) empty_geom
            """;

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            // Get sample IDs
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                SELECT {oidCol}
                FROM honua.{table}
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
        string scanLimitClause, int declaredSrid, int maxSamples,
        List<GeometryAnomaly> anomalies, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) AS cnt
            FROM (
                SELECT {oidCol}
                FROM honua.{table}
                WHERE {geomCol} IS NOT NULL AND ST_SRID({geomCol}) != @srid
                {scanLimitClause}
            ) srid_mismatch
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@srid", declaredSrid));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                SELECT {oidCol}
                FROM honua.{table}
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
        string scanLimitClause, int maxSamples, List<GeometryAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) AS cnt
            FROM (
                SELECT {oidCol}
                FROM honua.{table}
                WHERE {geomCol} IS NOT NULL
                  AND ST_GeometryType({geomCol}) IN ('ST_Polygon', 'ST_MultiPolygon')
                  AND ST_Perimeter({geomCol}::geography) > 0
                  AND ST_Area({geomCol}::geography) / ST_Perimeter({geomCol}::geography) < @ratio
                {scanLimitClause}
            ) suspicious
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@ratio", SuspiciousAreaPerimeterRatio));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                SELECT {oidCol}
                FROM honua.{table}
                WHERE {geomCol} IS NOT NULL
                  AND ST_GeometryType({geomCol}) IN ('ST_Polygon', 'ST_MultiPolygon')
                  AND ST_Perimeter({geomCol}::geography) > 0
                  AND ST_Area({geomCol}::geography) / ST_Perimeter({geomCol}::geography) < @ratio
                ORDER BY {oidCol}
                LIMIT {maxSamples}
                """;
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
        string scanLimitClause, int maxSamples, List<GeometryAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) AS cnt
            FROM (
                SELECT {oidCol}
                FROM honua.{table}
                WHERE {geomCol} IS NOT NULL
                  AND ST_NPoints({geomCol}) != ST_NPoints(ST_RemoveRepeatedPoints({geomCol}))
                {scanLimitClause}
            ) dups
            """;

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                SELECT {oidCol}
                FROM honua.{table}
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

        var table = SanitizeIdentifier(request.TableName);
        var oidCol = SanitizeIdentifier(request.ObjectIdColumn);
        var scanLimitClause = request.ScanLimit > 0 ? $" LIMIT {request.ScanLimit}" : "";

        foreach (var field in request.AttributeColumns)
        {
            var col = SanitizeIdentifier(field.Name);

            // 1. Null cluster detection
            await DetectNullClustersAsync(
                connection, table, col, oidCol, scanLimitClause,
                totalCount, field, request.MaxSampleFeatures, anomalies, cancellationToken);

            // 2. High cardinality for text fields
            if (field.DataType == AnomalyFieldDataType.Text)
            {
                await DetectHighCardinalityAsync(
                    connection, table, col, scanLimitClause,
                    totalCount, field, anomalies, cancellationToken);
            }

            // 3. Numeric outliers
            if (field.DataType == AnomalyFieldDataType.Numeric)
            {
                await DetectNumericOutliersAsync(
                    connection, table, col, oidCol, scanLimitClause,
                    field, request.MaxSampleFeatures, anomalies, cancellationToken);
            }
        }

        return anomalies;
    }

    private static async Task DetectNullClustersAsync(
        DbConnection connection, string table, string col, string oidCol,
        string scanLimitClause, long totalCount, AnomalyFieldDescriptor field,
        int maxSamples, List<AttributeAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) AS null_count
            FROM (
                SELECT {oidCol}
                FROM honua.{table}
                WHERE {col} IS NULL
                {scanLimitClause}
            ) nulls
            """;

        var nullCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        var effectiveTotal = totalCount > 0 ? totalCount : 1;
        var nullPercent = (int)(nullCount * 100 / effectiveTotal);

        if (nullPercent >= NullClusterThresholdPercent)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                SELECT {oidCol}
                FROM honua.{table}
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
        DbConnection connection, string table, string col,
        string scanLimitClause, long totalCount, AnomalyFieldDescriptor field,
        List<AttributeAnomaly> anomalies, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(DISTINCT {col}) AS distinct_count
            FROM (
                SELECT {col}
                FROM honua.{table}
                WHERE {col} IS NOT NULL
                {scanLimitClause}
            ) vals
            """;

        var distinctCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        var nonNullCount = totalCount; // Approximation; actual non-null may differ
        if (nonNullCount > 10 && distinctCount > 0)
        {
            var ratio = (double)distinctCount / nonNullCount;
            if (ratio >= HighCardinalityRatio)
            {
                anomalies.Add(new AttributeAnomaly
                {
                    Type = AttributeAnomalyType.HighCardinality,
                    FieldName = field.Name,
                    Reason = $"High cardinality: {distinctCount} distinct values out of ~{nonNullCount} records ({ratio:P0}). May be a unique identifier rather than a categorical field.",
                    Severity = AnomalySeverity.Info,
                    AffectedCount = (int)Math.Min(distinctCount, int.MaxValue),
                });
            }
        }
    }

    private static async Task DetectNumericOutliersAsync(
        DbConnection connection, string table, string col, string oidCol,
        string scanLimitClause, AnomalyFieldDescriptor field,
        int maxSamples, List<AttributeAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        // First compute mean and stddev
        await using var statsCmd = connection.CreateCommand();
        statsCmd.CommandText = $"""
            SELECT AVG({col}::double precision) AS mean,
                   STDDEV_POP({col}::double precision) AS stddev,
                   COUNT(*) AS cnt
            FROM (
                SELECT {col}
                FROM honua.{table}
                WHERE {col} IS NOT NULL
                {scanLimitClause}
            ) vals
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

        // Count outliers
        await using var outlierCmd = connection.CreateCommand();
        outlierCmd.CommandText = $"""
            SELECT COUNT(*) AS cnt
            FROM (
                SELECT {oidCol}
                FROM honua.{table}
                WHERE {col} IS NOT NULL
                  AND ({col}::double precision < @lower OR {col}::double precision > @upper)
                {scanLimitClause}
            ) outliers
            """;
        outlierCmd.Parameters.Add(new NpgsqlParameter("@lower", lowerBound));
        outlierCmd.Parameters.Add(new NpgsqlParameter("@upper", upperBound));

        var outlierCount = Convert.ToInt32(await outlierCmd.ExecuteScalarAsync(cancellationToken));
        if (outlierCount > 0)
        {
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = $"""
                SELECT {oidCol}
                FROM honua.{table}
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

    private static List<long> ReadSampleIds(DbDataReader reader, int columnIndex, int maxSamples)
    {
        if (reader.IsDBNull(columnIndex)) return [];
        if (reader.GetValue(columnIndex) is long[] ids)
        {
            return ids.Take(maxSamples).ToList();
        }
        if (reader.GetValue(columnIndex) is int[] intIds)
        {
            return intIds.Take(maxSamples).Select(i => (long)i).ToList();
        }
        return [];
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
    /// Sanitizes a SQL identifier to prevent injection.
    /// Only allows alphanumeric characters and underscores.
    /// </summary>
    private static string SanitizeIdentifier(string identifier)
    {
        foreach (var c in identifier)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException(
                    $"Invalid identifier character '{c}' in '{identifier}'.");
            }
        }
        return identifier;
    }
}
