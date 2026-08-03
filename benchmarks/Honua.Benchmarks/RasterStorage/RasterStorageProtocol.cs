// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Benchmarks.RasterStorage;

internal static class RasterStorageMetricNames
{
    public const string DatabaseCpuMilliseconds = "database.cpu.milliseconds";
    public const string DatabaseBlocksRead = "database.blocks.read";
    public const string DatabaseBlocksHit = "database.blocks.hit";
    public const string DatabaseTempBytes = "database.temp.bytes";
    public const string ObjectRequests = "object.requests";
    public const string ObjectBytesRead = "object.bytes.read";
    public const string LogicalRasterBytes = "storage.logical.bytes";
    public const string PhysicalStorageBytes = "storage.physical.bytes";
    public const string StorageAmplification = "storage.amplification.ratio";
    public const string IngestMilliseconds = "ingest.milliseconds";
    public const string BackupBytes = "backup.bytes";
    public const string BackupMilliseconds = "backup.milliseconds";
    public const string RestoreMilliseconds = "restore.milliseconds";
    public const string VacuumMilliseconds = "vacuum.milliseconds";

    public static readonly IReadOnlyList<string> Required =
    [
        DatabaseCpuMilliseconds,
        DatabaseBlocksRead,
        DatabaseBlocksHit,
        DatabaseTempBytes,
        ObjectRequests,
        ObjectBytesRead,
        LogicalRasterBytes,
        PhysicalStorageBytes,
        StorageAmplification,
        IngestMilliseconds,
        BackupBytes,
        BackupMilliseconds,
        RestoreMilliseconds,
        VacuumMilliseconds,
    ];
}

internal static class RasterStorageProtocol
{
    public const string Version = "honua-raster-storage-v1";

    public static RasterStorageProtocolDefinition Create()
    {
        var fixtures = CreateFixtures();
        var cells = CreateCells();
        var protocol = new RasterStorageProtocolDefinition(
            Version,
            fixtures,
            cells,
            RasterStorageMetricNames.Required,
            new RasterStorageThresholdContract(
                "Eligible only when measured p95 and concurrent-tenant p95 are within the operator serving SLO and database/object budgets.",
                "Eligible only when measured duration, database CPU, I/O, temp use, and storage amplification are within the configured durable-analysis budget.",
                "Prefer an authoritative object representation when restore time, backup bytes, and storage cost beat database residency and no unsupported serving/analysis cell is required.",
                [
                    "serving p50/p95 SLO by workload",
                    "database CPU, I/O, temp, connection, and concurrency budgets",
                    "object request, byte, latency, and cost budgets",
                    "maximum ingest and restore windows",
                    "maximum accepted storage amplification",
                ]));

        RasterStorageProtocolValidator.ValidateDefinition(protocol);
        return protocol;
    }

    private static IReadOnlyList<RasterFixtureDefinition> CreateFixtures()
    {
        const double webMercatorOrigin = -20037508.342789244;
        const double top = 20037508.342789244;
        const double scale = 9.554628535647032;

        return
        [
            new(
                "small-raster",
                "One 512 x 512 single-band scene for request-path and baseline-installation measurements.",
                "8BUI",
                1,
                [new RasterGrid(3857, webMercatorOrigin, top, scale, -scale, 0, 0, 512, 512)],
                GridExpectation.Aligned),
            new(
                "large-scene",
                "One 8192 x 8192 single-band scene (64 MiB logical pixels) for detoast, temp, and export pressure.",
                "8BUI",
                1,
                [new RasterGrid(3857, webMercatorOrigin, top, scale, -scale, 0, 0, 8192, 8192)],
                GridExpectation.Aligned),
            new(
                "aligned-mosaic",
                "Sixteen aligned 2048 x 2048 scenes in a 4 x 4 grid (64 MiB logical pixels).",
                "8BUI",
                1,
                CreateAlignedMosaic(webMercatorOrigin, top, scale, 2048, 4),
                GridExpectation.Aligned),
            new(
                "mixed-grid-mosaic",
                "Four scenes containing a half-pixel origin offset and a mismatched pixel scale; normalization is required before mosaic analysis.",
                "8BUI",
                1,
                CreateMixedGridMosaic(webMercatorOrigin, top, scale),
                GridExpectation.Misaligned),
        ];
    }

    private static List<RasterGrid> CreateAlignedMosaic(
        double originX,
        double originY,
        double scale,
        int sceneSize,
        int scenesPerAxis)
    {
        var scenes = new List<RasterGrid>(scenesPerAxis * scenesPerAxis);
        for (var row = 0; row < scenesPerAxis; row++)
        {
            for (var column = 0; column < scenesPerAxis; column++)
            {
                scenes.Add(new RasterGrid(
                    3857,
                    originX + (column * sceneSize * scale),
                    originY - (row * sceneSize * scale),
                    scale,
                    -scale,
                    0,
                    0,
                    sceneSize,
                    sceneSize));
            }
        }

        return scenes;
    }

    private static IReadOnlyList<RasterGrid> CreateMixedGridMosaic(double originX, double originY, double scale)
        =>
        [
            new RasterGrid(3857, originX, originY, scale, -scale, 0, 0, 1024, 1024),
            new RasterGrid(3857, originX + (1024 * scale), originY, scale, -scale, 0, 0, 1024, 1024),
            new RasterGrid(3857, originX, originY - (1024 * scale) + (scale / 2), scale, -scale, 0, 0, 1024, 1024),
            new RasterGrid(3857, originX + (1024 * scale), originY - (1024 * scale), scale * 2, -scale * 2, 0, 0, 512, 512),
        ];

    private static List<RasterStorageBenchmarkCell> CreateCells()
    {
        var cells = new List<RasterStorageBenchmarkCell>();
        foreach (var workload in Enum.GetValues<RasterStorageWorkload>())
        {
            var support = workload is RasterStorageWorkload.Backup or RasterStorageWorkload.Restore
                ? BenchmarkSupport.ExternalStep
                : BenchmarkSupport.Runnable;
            var reason = support == BenchmarkSupport.ExternalStep
                ? "Run the documented pg_dump/restore step outside the in-process adapter so server files and credentials are never copied through the benchmark process."
                : "The benchmark-only PostGIS adapter exercises this operation against an isolated scratch schema.";

            cells.Add(new RasterStorageBenchmarkCell(
                RasterStorageLayout.PostgisMonolithicExternal,
                workload,
                support,
                reason));
            cells.Add(new RasterStorageBenchmarkCell(
                RasterStorageLayout.PostgisTiled,
                workload,
                support,
                reason));
        }

        foreach (var workload in Enum.GetValues<RasterStorageWorkload>())
        {
            if (workload == RasterStorageWorkload.Tile)
            {
                cells.Add(new RasterStorageBenchmarkCell(
                    RasterStorageLayout.ObjectCog,
                    workload,
                    BenchmarkSupport.Runnable,
                    "The HTTP range adapter reads and decodes a real COG tile while counting object requests and bytes."));
            }
            else
            {
                cells.Add(new RasterStorageBenchmarkCell(
                    RasterStorageLayout.ObjectCog,
                    workload,
                    BenchmarkSupport.Unsupported,
                    "The current COG path does not expose this workload through the shared raster-store semantics; do not infer parity from tile range reads.",
                    "#3102"));
            }

            cells.Add(new RasterStorageBenchmarkCell(
                RasterStorageLayout.HybridCogPostgis,
                workload,
                BenchmarkSupport.Unsupported,
                "There is no production authority/materialization registry or cache lifecycle to benchmark without inventing behavior in the harness.",
                "#3098"));

            cells.Add(new RasterStorageBenchmarkCell(
                RasterStorageLayout.ObjectZarr,
                workload,
                BenchmarkSupport.Unsupported,
                "Persistent, versioned Zarr catalog and multi-object serving are not yet a production storage layout; bounded parser capability is not workload parity.",
                "#3103"));
        }

        return cells;
    }
}
