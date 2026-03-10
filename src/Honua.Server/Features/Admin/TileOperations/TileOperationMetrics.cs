// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Metrics emitted by tile operation jobs.
/// </summary>
internal static class TileOperationMetrics
{
    public static readonly UpDownCounter<long> QueueDepth = HonuaTelemetry.Meter.CreateUpDownCounter<long>(
        "honua.tile.jobs.queue_depth",
        "jobs",
        "Number of tile jobs waiting in queue.");

    public static readonly Counter<long> JobCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.tile.jobs.total",
        "jobs",
        "Number of tile jobs completed by status.");

    public static readonly Histogram<double> JobDurationMs = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.tile.jobs.duration_ms",
        "ms",
        "Tile job execution duration in milliseconds.");

    public static readonly Counter<long> TileThroughput = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.tile.jobs.tiles_processed",
        "tiles",
        "Number of tiles processed by tile jobs.");

    public static readonly Counter<long> ArchivesGenerated = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.tile.archives.total",
        "archives",
        "Number of PMTiles archives generated.");

    public static readonly Histogram<double> ArchiveSizeBytes = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.tile.archives.size_bytes",
        "bytes",
        "Size of generated PMTiles archives.");
}

