// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Benchmarks.RasterStorage;

internal static class RasterStorageMetrics
{
    public static IReadOnlyList<MetricObservation> ForPostgis(
        DatabaseMetricDelta database,
        long logicalBytes,
        long physicalBytes,
        double? ingestMilliseconds = null,
        double? vacuumMilliseconds = null)
    {
        var amplification = logicalBytes == 0 ? 0 : (double)physicalBytes / logicalBytes;
        return
        [
            Unavailable(
                RasterStorageMetricNames.DatabaseCpuMilliseconds,
                "ms",
                "PostgreSQL core does not expose per-query CPU; supply an isolated container/cgroup or pg_stat_kcache sampler before using this run for policy promotion."),
            Measured(RasterStorageMetricNames.DatabaseBlocksRead, database.BlocksRead, "blocks", "pg_stat_database delta"),
            Measured(RasterStorageMetricNames.DatabaseBlocksHit, database.BlocksHit, "blocks", "pg_stat_database delta"),
            Measured(RasterStorageMetricNames.DatabaseTempBytes, database.TempBytes, "bytes", "pg_stat_database delta"),
            NotApplicable(RasterStorageMetricNames.ObjectRequests, "requests", "PostGIS workload did not read an object store."),
            NotApplicable(RasterStorageMetricNames.ObjectBytesRead, "bytes", "PostGIS workload did not read an object store."),
            Measured(RasterStorageMetricNames.LogicalRasterBytes, logicalBytes, "bytes", "deterministic fixture dimensions"),
            Measured(RasterStorageMetricNames.PhysicalStorageBytes, physicalBytes, "bytes", "pg_total_relation_size"),
            Measured(RasterStorageMetricNames.StorageAmplification, amplification, "ratio", "physical bytes / logical pixel bytes"),
            Optional(RasterStorageMetricNames.IngestMilliseconds, ingestMilliseconds, "ms", "adapter stopwatch", "This workload did not ingest data."),
            Unavailable(RasterStorageMetricNames.BackupBytes, "bytes", "Capture from the external pg_dump protocol step."),
            Unavailable(RasterStorageMetricNames.BackupMilliseconds, "ms", "Capture from the external pg_dump protocol step."),
            Unavailable(RasterStorageMetricNames.RestoreMilliseconds, "ms", "Capture from the external restore protocol step."),
            Optional(RasterStorageMetricNames.VacuumMilliseconds, vacuumMilliseconds, "ms", "adapter stopwatch", "This workload did not run VACUUM."),
        ];
    }

    public static IReadOnlyList<MetricObservation> ForCog(
        double objectRequests,
        double objectBytesRead,
        long logicalBytes,
        long physicalBytes)
    {
        var amplification = logicalBytes == 0 ? 0 : (double)physicalBytes / logicalBytes;
        return
        [
            NotApplicable(RasterStorageMetricNames.DatabaseCpuMilliseconds, "ms", "Direct COG tile read did not execute database raster work."),
            NotApplicable(RasterStorageMetricNames.DatabaseBlocksRead, "blocks", "Direct COG tile read did not execute database raster work."),
            NotApplicable(RasterStorageMetricNames.DatabaseBlocksHit, "blocks", "Direct COG tile read did not execute database raster work."),
            NotApplicable(RasterStorageMetricNames.DatabaseTempBytes, "bytes", "Direct COG tile read did not execute database raster work."),
            Measured(RasterStorageMetricNames.ObjectRequests, objectRequests, "requests/op", "counting HTTP range reader, mean per measured operation"),
            Measured(RasterStorageMetricNames.ObjectBytesRead, objectBytesRead, "bytes/op", "counting HTTP range reader, mean per measured operation"),
            Measured(RasterStorageMetricNames.LogicalRasterBytes, logicalBytes, "bytes", "COG metadata dimensions"),
            Measured(RasterStorageMetricNames.PhysicalStorageBytes, physicalBytes, "bytes", "HTTP Content-Length/Content-Range"),
            Measured(RasterStorageMetricNames.StorageAmplification, amplification, "ratio", "object bytes / logical pixel bytes"),
            NotApplicable(RasterStorageMetricNames.IngestMilliseconds, "ms", "COG construction/upload is outside the bounded serving adapter."),
            Unavailable(RasterStorageMetricNames.BackupBytes, "bytes", "Capture from the object-version replication protocol step."),
            Unavailable(RasterStorageMetricNames.BackupMilliseconds, "ms", "Capture from the object-version replication protocol step."),
            Unavailable(RasterStorageMetricNames.RestoreMilliseconds, "ms", "Capture from the object-version restore protocol step."),
            NotApplicable(RasterStorageMetricNames.VacuumMilliseconds, "ms", "Object storage has no VACUUM operation."),
        ];
    }

    private static MetricObservation Measured(string name, double value, string unit, string source)
        => new(name, value, unit, MetricAvailability.Measured, source);

    private static MetricObservation Optional(
        string name,
        double? value,
        string unit,
        string source,
        string missingReason)
        => value is null ? Unavailable(name, unit, missingReason) : Measured(name, value.Value, unit, source);

    private static MetricObservation NotApplicable(string name, string unit, string reason)
        => new(name, null, unit, MetricAvailability.NotApplicable, "not applicable", reason);

    private static MetricObservation Unavailable(string name, string unit, string reason)
        => new(name, null, unit, MetricAvailability.Unavailable, "not captured", reason);
}

internal readonly record struct DatabaseMetricDelta(long BlocksRead, long BlocksHit, long TempBytes);
